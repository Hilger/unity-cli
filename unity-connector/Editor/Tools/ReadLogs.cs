using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "read_logs",
        Description = "Read Unity console logs from the native ring buffer. " +
                      "More reliable than 'console' — no reflection, thread-safe, survives domain reloads.")]
    public static class ReadLogs
    {
        public class Parameters
        {
            [ToolParameter("Sequence number to read from (exclusive). Use 0 for all available.")]
            public int SinceSeq { get; set; }

            [ToolParameter("Comma-separated log types: log, warning, error, exception, assert. Default: all")]
            public string Type { get; set; }

            [ToolParameter("Maximum number of entries to return (0 = no limit)")]
            public int Lines { get; set; }

            [ToolParameter("Clear log buffer")]
            public bool Clear { get; set; }

            [ToolParameter("Get current sequence number only")]
            public bool SeqOnly { get; set; }
        }

        static readonly string[] LogTypeNames = { "Log", "Warning", "Error", "Exception", "Assert" };

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());

            if (p.GetBool("clear"))
            {
                NativeLogs.Clear();
                return new SuccessResponse("Log buffer cleared.");
            }

            if (p.GetBool("seq_only"))
            {
                return new SuccessResponse("Current sequence",
                    new { seq = NativeLogs.CurrentSeq });
            }

            int sinceSeq = p.GetInt("since_seq") ?? 0;
            int maxCount = p.GetInt("lines") ?? 0;
            var filter = ParseTypeFilter(p.Get("type", "all"));

            var logs = NativeLogs.Read(sinceSeq, filter, maxCount);
            int currentSeq = NativeLogs.CurrentSeq;

            var logsArray = new JArray();
            foreach (var log in logs)
            {
                string typeName = log.logType >= 0 && log.logType < LogTypeNames.Length
                    ? LogTypeNames[log.logType]
                    : "Unknown";

                logsArray.Add(new JObject
                {
                    ["seq"] = log.seq,
                    ["type"] = typeName,
                    ["ts"] = log.timestampMs,
                    ["message"] = log.message ?? "",
                    ["stacktrace"] = log.stacktrace ?? ""
                });
            }

            return new SuccessResponse($"Read {logs.Count} log entries", new
            {
                seq = currentSeq,
                logs = logsArray
            });
        }

        static NativeLogs.LogTypeFilter ParseTypeFilter(string types)
        {
            if (string.IsNullOrEmpty(types) || types == "all")
                return NativeLogs.LogTypeFilter.All;

            NativeLogs.LogTypeFilter filter = 0;
            foreach (string t in types.Split(','))
            {
                switch (t.Trim().ToLowerInvariant())
                {
                    case "log":       filter |= NativeLogs.LogTypeFilter.Log; break;
                    case "warning":   filter |= NativeLogs.LogTypeFilter.Warning; break;
                    case "error":     filter |= NativeLogs.LogTypeFilter.Error; break;
                    case "exception": filter |= NativeLogs.LogTypeFilter.Exception; break;
                    case "assert":    filter |= NativeLogs.LogTypeFilter.Assert; break;
                }
            }
            return filter == 0 ? NativeLogs.LogTypeFilter.All : filter;
        }
    }
}
