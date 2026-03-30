using System;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Managed bridge to the native TCP server plugin.
    /// The native plugin (native_server.c) holds the listening socket permanently —
    /// it survives domain reloads because native plugins are never unloaded.
    /// This class polls for buffered requests on EditorApplication.update,
    /// dispatches them to CommandRouter, and sends responses back through native.
    /// </summary>
    [InitializeOnLoad]
    public static class HttpServer
    {
        const string LIB = "native_server";
        const int REQUEST_BUFFER_SIZE = 256 * 1024;

        [DllImport(LIB)] static extern int native_server_get_port();
        [DllImport(LIB)] static extern int native_server_is_running();
        [DllImport(LIB)] static extern int native_server_get_request_count();
        [DllImport(LIB)] static extern int native_server_get_pending_request(byte[] buffer, int bufferSize);
        [DllImport(LIB)] static extern int native_server_send_response(int slotId, byte[] json, int jsonLen);
        [DllImport(LIB)] static extern int native_server_send_error(int slotId, int statusCode, byte[] json, int jsonLen);

        static bool s_Registered;

        static HttpServer()
        {
            Register();
        }

        static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            EditorApplication.update += PollNative;

            var port = Port;
            if (port > 0)
                Debug.Log($"[UnityCliConnector] Native HTTP server on port {port}");
            else
                Debug.LogWarning("[UnityCliConnector] Native server not running — plugin may not be loaded");
        }

        /// <summary>Port the native server is listening on (0 if not running).</summary>
        public static int Port
        {
            get
            {
                try { return native_server_get_port(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Called ~60x/sec by EditorApplication.update.
        /// Dequeues one request per frame from the native buffer and processes it.
        /// </summary>
        static void PollNative()
        {
            try
            {
                if (native_server_is_running() == 0) return;
                if (native_server_get_request_count() == 0) return;
            }
            catch
            {
                return; // native plugin not loaded yet
            }

            var buffer = new byte[REQUEST_BUFFER_SIZE];
            int slotId = native_server_get_pending_request(buffer, buffer.Length);
            if (slotId == 0) return;

            // Parse the JSON body
            string bodyStr = Encoding.UTF8.GetString(buffer).TrimEnd('\0');

            ProcessRequest(slotId, bodyStr);
        }

        static async void ProcessRequest(int slotId, string body)
        {
            object result;
            int statusCode = 200;

            try
            {
                var json = JObject.Parse(body);
                var command = json["command"]?.ToString();
                var parameters = json["params"] as JObject;

                if (string.IsNullOrEmpty(command))
                {
                    result = new ErrorResponse("Missing 'command' field");
                    statusCode = 400;
                }
                else
                {
                    result = await CommandRouter.Dispatch(command, parameters);
                }
            }
            catch (Exception ex)
            {
                result = new ErrorResponse($"Request error: {ex.Message}");
                statusCode = 500;
            }

            // Serialize and send response
            var responseJson = JsonConvert.SerializeObject(result);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);

            try
            {
                if (statusCode == 200)
                    native_server_send_response(slotId, responseBytes, responseBytes.Length);
                else
                    native_server_send_error(slotId, statusCode, responseBytes, responseBytes.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliConnector] Failed to send response for slot {slotId}: {ex.Message}");
            }
        }
    }
}
