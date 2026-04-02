using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityCliConnector
{
    /// <summary>
    /// Ring buffer for Unity console log messages, backed by native plugin memory.
    /// Replaces the fragile reflection-based LogEntries access used by ReadConsole.
    /// Thread-safe — C# hooks Application.logMessageReceivedThreaded which fires on any thread.
    /// </summary>
    public static class NativeLogs
    {
        const string LIB = "native_server";
        const int READ_BUFFER_SIZE = 512 * 1024;

        [Flags]
        public enum LogTypeFilter
        {
            Log       = 1 << 0,
            Warning   = 1 << 1,
            Error     = 1 << 2,
            Exception = 1 << 3,
            Assert    = 1 << 4,
            All       = Log | Warning | Error | Exception | Assert
        }

        public struct LogRecord
        {
            public int seq;
            public int logType;
            public long timestampMs;
            public string message;
            public string stacktrace;
        }

        [DllImport(LIB)]
        static extern int native_server_log_push(int logType,
            byte[] msg, int msgLen, byte[] stack, int stackLen);

        [DllImport(LIB)]
        static extern int native_server_log_read(int sinceSeq, int typeFilter, int maxCount,
            byte[] buffer, int bufferSize);

        [DllImport(LIB)]
        static extern int native_server_log_get_seq();

        [DllImport(LIB)]
        static extern void native_server_log_clear();

        static byte[] s_ReadBuffer;

        /// <summary>Push a log entry into the native ring buffer.</summary>
        public static int Push(int logType, string message, string stackTrace)
        {
            byte[] msgBytes = string.IsNullOrEmpty(message) ? null : Encoding.UTF8.GetBytes(message);
            byte[] stackBytes = string.IsNullOrEmpty(stackTrace) ? null : Encoding.UTF8.GetBytes(stackTrace);
            return native_server_log_push(logType,
                msgBytes, msgBytes?.Length ?? 0,
                stackBytes, stackBytes?.Length ?? 0);
        }

        /// <summary>Read log entries since sinceSeq, with optional type filter and count limit.</summary>
        public static List<LogRecord> Read(int sinceSeq,
            LogTypeFilter filter = LogTypeFilter.All, int maxCount = 0)
        {
            if (s_ReadBuffer == null)
                s_ReadBuffer = new byte[READ_BUFFER_SIZE];

            int count = native_server_log_read(sinceSeq, (int)filter, maxCount,
                s_ReadBuffer, s_ReadBuffer.Length);

            var results = new List<LogRecord>();
            if (count <= 0) return results;

            // Parse binary protocol:
            // [4: count] per entry: [4: seq] [4: log_type] [8: ts]
            //   [4: msg_len] [msg_len: message] [4: stack_len] [stack_len: stacktrace]
            int pos = 4; // skip count header
            for (int i = 0; i < count; i++)
            {
                if (pos + 24 > s_ReadBuffer.Length) break;

                var rec = new LogRecord();
                rec.seq = BitConverter.ToInt32(s_ReadBuffer, pos); pos += 4;
                rec.logType = BitConverter.ToInt32(s_ReadBuffer, pos); pos += 4;
                rec.timestampMs = BitConverter.ToInt64(s_ReadBuffer, pos); pos += 8;

                int msgLen = BitConverter.ToInt32(s_ReadBuffer, pos); pos += 4;
                if (msgLen > 0 && pos + msgLen <= s_ReadBuffer.Length)
                {
                    rec.message = Encoding.UTF8.GetString(s_ReadBuffer, pos, msgLen);
                    pos += msgLen;
                }

                int stackLen = BitConverter.ToInt32(s_ReadBuffer, pos); pos += 4;
                if (stackLen > 0 && pos + stackLen <= s_ReadBuffer.Length)
                {
                    rec.stacktrace = Encoding.UTF8.GetString(s_ReadBuffer, pos, stackLen);
                    pos += stackLen;
                }

                results.Add(rec);
            }

            return results;
        }

        /// <summary>Current highest log sequence number.</summary>
        public static int CurrentSeq
        {
            get
            {
                try { return native_server_log_get_seq(); }
                catch { return 0; }
            }
        }

        /// <summary>Clear all log entries.</summary>
        public static void Clear()
        {
            try { native_server_log_clear(); }
            catch { /* native plugin not loaded */ }
        }
    }
}
