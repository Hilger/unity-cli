/*
 * native_server.c — Persistent TCP HTTP server for Unity CLI Connector
 *
 * This native plugin holds a TCP listening socket that survives Unity domain
 * reloads. The managed C# side polls for requests via get_pending_request()
 * and sends responses via send_response().
 *
 * Architecture:
 *   - UnityPluginLoad: create socket, bind, listen, spawn accept thread
 *   - Accept thread (pthread): runs continuously, accepts connections,
 *     reads full HTTP requests, buffers them for managed code
 *   - Managed code (EditorApplication.update): polls get_pending_request(),
 *     processes command, calls send_response()
 *   - UnityPluginUnload: signal shutdown, join thread, close socket
 */

#ifdef _WIN32
  #define WIN32_LEAN_AND_MEAN
  #include <winsock2.h>
  #include <ws2tcpip.h>
  #pragma comment(lib, "ws2_32.lib")
  typedef SOCKET socket_t;
  #define INVALID_SOCK INVALID_SOCKET
  #define CLOSESOCK closesocket
  #define SOCKERR WSAGetLastError()
  #define EWOULDBLOCK_VAL WSAEWOULDBLOCK
#else
  #include <sys/socket.h>
  #include <netinet/in.h>
  #include <arpa/inet.h>
  #include <unistd.h>
  #include <fcntl.h>
  #include <poll.h>
  #include <errno.h>
  #include <string.h>
  typedef int socket_t;
  #define INVALID_SOCK (-1)
  #define CLOSESOCK close
  #define SOCKERR errno
  #define EWOULDBLOCK_VAL EWOULDBLOCK
#endif

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

#ifdef _WIN32
  #include <windows.h>
  typedef HANDLE thread_t;
  typedef CRITICAL_SECTION mutex_t;
  #define MUTEX_INIT(m)   InitializeCriticalSection(m)
  #define MUTEX_LOCK(m)   EnterCriticalSection(m)
  #define MUTEX_UNLOCK(m) LeaveCriticalSection(m)
  #define MUTEX_DESTROY(m) DeleteCriticalSection(m)
#else
  #include <pthread.h>
  typedef pthread_t thread_t;
  typedef pthread_mutex_t mutex_t;
  #define MUTEX_INIT(m)   pthread_mutex_init(m, NULL)
  #define MUTEX_LOCK(m)   pthread_mutex_lock(m)
  #define MUTEX_UNLOCK(m) pthread_mutex_unlock(m)
  #define MUTEX_DESTROY(m) pthread_mutex_destroy(m)
#endif

/* ---- Configuration ---- */
#define DEFAULT_PORT       8090
#define MAX_PORT_ATTEMPTS  10
#define MAX_PENDING        64
#define MAX_REQUEST_SIZE   (256 * 1024)  /* 256 KB per request */
#define MAX_RESPONSE_SIZE  (1024 * 1024) /* 1 MB per response */
#define RECV_BUF_SIZE      (64 * 1024)

/* ---- Export macros ---- */
#ifdef _WIN32
  #define EXPORT __declspec(dllexport)
#else
  #define EXPORT __attribute__((visibility("default")))
#endif

/* ---- Slot: one pending request or in-flight response ---- */
typedef struct {
    int      used;           /* 0=free, 1=has_request, 2=has_response */
    int      slot_id;        /* unique id for this request */
    socket_t client_sock;    /* the accepted connection (kept open until response sent) */
    char     request[MAX_REQUEST_SIZE];
    int      request_len;
    char     response[MAX_RESPONSE_SIZE];
    int      response_len;
} Slot;

/* ---- Global state (persists across domain reloads) ---- */
static socket_t   g_listen_sock = INVALID_SOCK;
static int        g_port        = 0;
static int        g_running     = 0;
static thread_t   g_thread;
static mutex_t    g_mutex;
static Slot       g_slots[MAX_PENDING];
static int        g_next_id     = 1;

/* ---- Platform helpers ---- */

static void set_nonblocking(socket_t sock) {
#ifdef _WIN32
    u_long mode = 1;
    ioctlsocket(sock, FIONBIO, &mode);
#else
    int flags = fcntl(sock, F_GETFL, 0);
    fcntl(sock, F_SETFL, flags | O_NONBLOCK);
#endif
}

/* Read the HTTP body from a raw request buffer.
 * Returns pointer to body start, or NULL if headers incomplete.
 * Sets *body_len to Content-Length value. */
static const char* find_http_body(const char* buf, int buf_len, int* body_len) {
    const char* hdr_end = NULL;
    /* Search for \r\n\r\n */
    for (int i = 0; i < buf_len - 3; i++) {
        if (buf[i] == '\r' && buf[i+1] == '\n' && buf[i+2] == '\r' && buf[i+3] == '\n') {
            hdr_end = buf + i + 4;
            break;
        }
    }
    if (!hdr_end) return NULL;

    /* Parse Content-Length */
    *body_len = 0;
    const char* cl = NULL;
    for (const char* p = buf; p < hdr_end - 16; p++) {
        if ((*p == 'C' || *p == 'c') &&
            (strncasecmp(p, "Content-Length:", 15) == 0 ||
             strncasecmp(p, "content-length:", 15) == 0)) {
            cl = p + 15;
            while (*cl == ' ') cl++;
            *body_len = atoi(cl);
            break;
        }
    }

    return hdr_end;
}

#ifndef _WIN32
  /* strncasecmp is POSIX, available on macOS/Linux */
#else
  #define strncasecmp _strnicmp
#endif

/* ---- Accept loop (runs on native thread) ---- */

static void handle_connection(socket_t client) {
    char buf[MAX_REQUEST_SIZE];
    int total = 0;

    /* Read until we have full headers + body */
    while (total < MAX_REQUEST_SIZE - 1) {
#ifdef _WIN32
        struct pollfd pfd = { client, POLLIN, 0 };
        int ready = WSAPoll(&pfd, 1, 5000);
#else
        struct pollfd pfd = { client, POLLIN, 0 };
        int ready = poll(&pfd, 1, 5000);
#endif
        if (ready <= 0) break; /* timeout or error */

        int n = recv(client, buf + total, MAX_REQUEST_SIZE - 1 - total, 0);
        if (n <= 0) break;
        total += n;
        buf[total] = '\0';

        /* Check if we have the complete request */
        int body_len = 0;
        const char* body = find_http_body(buf, total, &body_len);
        if (body) {
            int header_size = (int)(body - buf);
            if (total >= header_size + body_len) {
                /* Complete request received — queue it */
                MUTEX_LOCK(&g_mutex);
                int queued = 0;
                for (int i = 0; i < MAX_PENDING; i++) {
                    if (g_slots[i].used == 0) {
                        g_slots[i].used = 1;
                        g_slots[i].slot_id = g_next_id++;
                        g_slots[i].client_sock = client;
                        /* Store just the body (the JSON payload) */
                        int copy_len = body_len < MAX_REQUEST_SIZE - 1 ? body_len : MAX_REQUEST_SIZE - 1;
                        memcpy(g_slots[i].request, body, copy_len);
                        g_slots[i].request[copy_len] = '\0';
                        g_slots[i].request_len = copy_len;
                        g_slots[i].response_len = 0;
                        queued = 1;
                        break;
                    }
                }
                MUTEX_UNLOCK(&g_mutex);

                if (!queued) {
                    /* All slots full — reject with 503 */
                    const char* reject =
                        "HTTP/1.1 503 Service Unavailable\r\n"
                        "Content-Length: 0\r\n"
                        "Connection: close\r\n\r\n";
                    send(client, reject, (int)strlen(reject), 0);
                    CLOSESOCK(client);
                }
                return; /* don't close — managed side will close after response */
            }
        }
    }

    /* Incomplete or bad request */
    CLOSESOCK(client);
}

#ifdef _WIN32
static DWORD WINAPI accept_thread(LPVOID arg) {
#else
static void* accept_thread(void* arg) {
#endif
    (void)arg;
    while (g_running) {
#ifdef _WIN32
        struct pollfd pfd = { g_listen_sock, POLLIN, 0 };
        int ready = WSAPoll(&pfd, 1, 200);
#else
        struct pollfd pfd = { g_listen_sock, POLLIN, 0 };
        int ready = poll(&pfd, 1, 200); /* 200ms timeout for shutdown check */
#endif
        if (ready <= 0) continue;

        struct sockaddr_in addr;
        socklen_t addr_len = sizeof(addr);
        socket_t client = accept(g_listen_sock, (struct sockaddr*)&addr, &addr_len);
        if (client == INVALID_SOCK) continue;

        handle_connection(client);
    }
    return 0;
}

/* ---- Exported API ---- */

EXPORT void UnityPluginLoad(void* unityInterfaces) {
    (void)unityInterfaces;

    if (g_listen_sock != INVALID_SOCK) return; /* already running */

#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif

    MUTEX_INIT(&g_mutex);
    memset(g_slots, 0, sizeof(g_slots));

    /* Try ports 8090..8099 */
    for (int attempt = 0; attempt < MAX_PORT_ATTEMPTS; attempt++) {
        int port = DEFAULT_PORT + attempt;

        socket_t sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (sock == INVALID_SOCK) continue;

        int reuse = 1;
        setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse));

        struct sockaddr_in addr;
        memset(&addr, 0, sizeof(addr));
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        addr.sin_port = htons(port);

        if (bind(sock, (struct sockaddr*)&addr, sizeof(addr)) == 0) {
            if (listen(sock, 8) == 0) {
                set_nonblocking(sock);
                g_listen_sock = sock;
                g_port = port;
                g_running = 1;

                /* Spawn accept thread */
#ifdef _WIN32
                g_thread = CreateThread(NULL, 0, accept_thread, NULL, 0, NULL);
#else
                pthread_create(&g_thread, NULL, accept_thread, NULL);
#endif
                return;
            }
        }
        CLOSESOCK(sock);
    }
}

EXPORT void UnityPluginUnload(void) {
    if (g_listen_sock == INVALID_SOCK) return;

    g_running = 0;

    /* Join accept thread */
#ifdef _WIN32
    WaitForSingleObject(g_thread, 3000);
    CloseHandle(g_thread);
#else
    pthread_join(g_thread, NULL);
#endif

    /* Close listening socket */
    CLOSESOCK(g_listen_sock);
    g_listen_sock = INVALID_SOCK;

    /* Close any pending client connections */
    MUTEX_LOCK(&g_mutex);
    for (int i = 0; i < MAX_PENDING; i++) {
        if (g_slots[i].used && g_slots[i].client_sock != INVALID_SOCK) {
            CLOSESOCK(g_slots[i].client_sock);
        }
        g_slots[i].used = 0;
    }
    MUTEX_UNLOCK(&g_mutex);
    MUTEX_DESTROY(&g_mutex);
    g_port = 0;

#ifdef _WIN32
    WSACleanup();
#endif
}

/* Returns the port the server is listening on, or 0 if not running. */
EXPORT int native_server_get_port(void) {
    return g_port;
}

/* Returns the number of pending requests waiting to be processed. */
EXPORT int native_server_get_request_count(void) {
    int count = 0;
    MUTEX_LOCK(&g_mutex);
    for (int i = 0; i < MAX_PENDING; i++) {
        if (g_slots[i].used == 1) count++;
    }
    MUTEX_UNLOCK(&g_mutex);
    return count;
}

/*
 * Copies the oldest pending request body into `buffer` (up to buffer_size bytes).
 * Returns the slot_id (>0) if a request was dequeued, or 0 if none pending.
 * The caller must later call native_server_send_response() with the same slot_id.
 */
EXPORT int native_server_get_pending_request(char* buffer, int buffer_size) {
    int result_id = 0;
    MUTEX_LOCK(&g_mutex);
    /* Find the slot with the lowest slot_id that has a request */
    int best = -1;
    int best_id = 0x7FFFFFFF;
    for (int i = 0; i < MAX_PENDING; i++) {
        if (g_slots[i].used == 1 && g_slots[i].slot_id < best_id) {
            best = i;
            best_id = g_slots[i].slot_id;
        }
    }
    if (best >= 0) {
        int copy_len = g_slots[best].request_len;
        if (copy_len > buffer_size - 1) copy_len = buffer_size - 1;
        memcpy(buffer, g_slots[best].request, copy_len);
        buffer[copy_len] = '\0';
        g_slots[best].used = 2; /* mark as in-flight (waiting for response) */
        result_id = g_slots[best].slot_id;
    }
    MUTEX_UNLOCK(&g_mutex);
    return result_id;
}

/*
 * Sends an HTTP 200 response with the given JSON body for the specified slot_id.
 * Closes the client connection. Returns 1 on success, 0 if slot not found.
 */
EXPORT int native_server_send_response(int slot_id, const char* json_body, int json_len) {
    MUTEX_LOCK(&g_mutex);
    int found = -1;
    for (int i = 0; i < MAX_PENDING; i++) {
        if (g_slots[i].used == 2 && g_slots[i].slot_id == slot_id) {
            found = i;
            break;
        }
    }
    if (found < 0) {
        MUTEX_UNLOCK(&g_mutex);
        return 0;
    }

    socket_t client = g_slots[found].client_sock;
    g_slots[found].used = 0;
    g_slots[found].client_sock = INVALID_SOCK;
    MUTEX_UNLOCK(&g_mutex);

    /* Build HTTP response */
    char header[256];
    int hdr_len = snprintf(header, sizeof(header),
        "HTTP/1.1 200 OK\r\n"
        "Content-Type: application/json\r\n"
        "Content-Length: %d\r\n"
        "Connection: close\r\n"
        "\r\n", json_len);

    send(client, header, hdr_len, 0);
    if (json_len > 0) {
        send(client, json_body, json_len, 0);
    }
    CLOSESOCK(client);
    return 1;
}

/*
 * Sends an HTTP error response (e.g., 400, 403) and closes the connection.
 */
EXPORT int native_server_send_error(int slot_id, int status_code, const char* json_body, int json_len) {
    MUTEX_LOCK(&g_mutex);
    int found = -1;
    for (int i = 0; i < MAX_PENDING; i++) {
        if (g_slots[i].used == 2 && g_slots[i].slot_id == slot_id) {
            found = i;
            break;
        }
    }
    if (found < 0) {
        MUTEX_UNLOCK(&g_mutex);
        return 0;
    }

    socket_t client = g_slots[found].client_sock;
    g_slots[found].used = 0;
    g_slots[found].client_sock = INVALID_SOCK;
    MUTEX_UNLOCK(&g_mutex);

    const char* reason = "Error";
    if (status_code == 400) reason = "Bad Request";
    else if (status_code == 403) reason = "Forbidden";
    else if (status_code == 500) reason = "Internal Server Error";
    else if (status_code == 503) reason = "Service Unavailable";

    char header[256];
    int hdr_len = snprintf(header, sizeof(header),
        "HTTP/1.1 %d %s\r\n"
        "Content-Type: application/json\r\n"
        "Content-Length: %d\r\n"
        "Connection: close\r\n"
        "\r\n", status_code, reason, json_len);

    send(client, header, hdr_len, 0);
    if (json_len > 0) {
        send(client, json_body, json_len, 0);
    }
    CLOSESOCK(client);
    return 1;
}

/* Returns 1 if the native server is running and listening. */
EXPORT int native_server_is_running(void) {
    return g_listen_sock != INVALID_SOCK && g_running;
}
