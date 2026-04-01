using System.Runtime.InteropServices;
using System.Text;

namespace UnityCliConnector
{
    /// <summary>
    /// Key-value data store backed by the native plugin.
    /// Survives C# domain reloads because the data lives in native memory.
    /// Used by the test runner to persist runId and metadata across reloads.
    /// </summary>
    public static class NativeData
    {
        const string LIB = "native_server";
        const int MAX_VALUE_SIZE = 512 * 1024;

        [DllImport(LIB, CharSet = CharSet.Ansi)]
        static extern int native_server_data_set(string key, byte[] value, int valueLen);

        [DllImport(LIB, CharSet = CharSet.Ansi)]
        static extern int native_server_data_get(string key, byte[] buffer, int bufferSize);

        [DllImport(LIB, CharSet = CharSet.Ansi)]
        static extern int native_server_data_delete(string key);

        [DllImport(LIB)]
        static extern void native_server_data_clear();

        public static bool Set(string key, string value)
        {
            var valBytes = Encoding.UTF8.GetBytes(value ?? "");
            return native_server_data_set(key, valBytes, valBytes.Length) == 1;
        }

        public static string Get(string key)
        {
            var buffer = new byte[MAX_VALUE_SIZE];
            int len = native_server_data_get(key, buffer, buffer.Length);
            if (len <= 0) return null;
            return Encoding.UTF8.GetString(buffer, 0, len);
        }

        public static bool Delete(string key)
        {
            return native_server_data_delete(key) == 1;
        }

        public static void Clear()
        {
            native_server_data_clear();
        }

        // --- Native string set (deduplication) ---

        [DllImport(LIB, CharSet = CharSet.Ansi)]
        static extern int native_server_set_add(string key);

        [DllImport(LIB)]
        static extern void native_server_set_clear();

        /// <summary>
        /// Add a string to the native set. Returns true if newly added,
        /// false if already present. Survives domain reloads.
        /// </summary>
        public static bool SetAdd(string key)
        {
            return native_server_set_add(key) == 1;
        }

        /// <summary>Clear the native set.</summary>
        public static void SetClear()
        {
            native_server_set_clear();
        }
    }
}
