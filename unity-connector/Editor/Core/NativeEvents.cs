using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityCliConnector
{
    /// <summary>
    /// Ring buffer for typed events, backed by native plugin memory.
    /// Survives C# domain reloads. C# pushes events via Push(),
    /// CLI polls via the poll_events tool which calls Poll().
    /// Uses a binary protocol between native and managed code.
    /// </summary>
    public static class NativeEvents
    {
        const string LIB = "native_server";
        const int POLL_BUFFER_SIZE = 512 * 1024;

        public enum EventType
        {
            StateChange  = 1,
            LogMessage   = 2,
            CompileStart = 3,
            CompileEnd   = 4,
            TestResult   = 5,
            PlayMode     = 6,
            DomainReload = 7,
            Custom       = 100
        }

        public struct EventRecord
        {
            public int seq;
            public int type;
            public long timestampMs;
            public string data;
        }

        [DllImport(LIB)]
        static extern int native_server_event_push(int type, byte[] data, int dataLen);

        [DllImport(LIB)]
        static extern int native_server_event_poll(int sinceSeq, byte[] buffer, int bufferSize);

        [DllImport(LIB)]
        static extern int native_server_event_get_seq();

        [DllImport(LIB)]
        static extern void native_server_event_clear();

        static byte[] s_PollBuffer;

        /// <summary>Push a typed event with a JSON data payload.</summary>
        public static int Push(EventType type, string jsonData)
        {
            byte[] data = null;
            int len = 0;
            if (!string.IsNullOrEmpty(jsonData))
            {
                data = Encoding.UTF8.GetBytes(jsonData);
                len = data.Length;
            }
            return native_server_event_push((int)type, data, len);
        }

        /// <summary>Poll events since the given sequence number. Returns parsed records.</summary>
        public static List<EventRecord> Poll(int sinceSeq)
        {
            if (s_PollBuffer == null)
                s_PollBuffer = new byte[POLL_BUFFER_SIZE];

            int count = native_server_event_poll(sinceSeq, s_PollBuffer, s_PollBuffer.Length);
            var results = new List<EventRecord>();
            if (count <= 0) return results;

            // Parse binary protocol:
            // [4: count] per event: [4: seq] [4: type] [8: ts] [4: data_len] [data_len: data]
            int pos = 4; // skip count header
            for (int i = 0; i < count; i++)
            {
                if (pos + 20 > s_PollBuffer.Length) break;

                var rec = new EventRecord();
                rec.seq = BitConverter.ToInt32(s_PollBuffer, pos); pos += 4;
                rec.type = BitConverter.ToInt32(s_PollBuffer, pos); pos += 4;
                rec.timestampMs = BitConverter.ToInt64(s_PollBuffer, pos); pos += 8;
                int dataLen = BitConverter.ToInt32(s_PollBuffer, pos); pos += 4;

                if (dataLen > 0 && pos + dataLen <= s_PollBuffer.Length)
                {
                    rec.data = Encoding.UTF8.GetString(s_PollBuffer, pos, dataLen);
                    pos += dataLen;
                }

                results.Add(rec);
            }

            return results;
        }

        /// <summary>Current highest event sequence number.</summary>
        public static int CurrentSeq
        {
            get
            {
                try { return native_server_event_get_seq(); }
                catch { return 0; }
            }
        }

        /// <summary>Clear all events and reset sequence counter.</summary>
        public static void Clear()
        {
            try { native_server_event_clear(); }
            catch { /* native plugin not loaded */ }
        }
    }
}
