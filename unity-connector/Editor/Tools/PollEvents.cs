using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "poll_events",
        Description = "Poll typed events from the native ring buffer since a given sequence number.")]
    public static class PollEvents
    {
        public class Parameters
        {
            [ToolParameter("Sequence number to poll from (exclusive). Use 0 for all available events.")]
            public int SinceSeq { get; set; }

            [ToolParameter("Clear events after polling")]
            public bool Clear { get; set; }

            [ToolParameter("Get current sequence number only (no events returned)")]
            public bool SeqOnly { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());

            if (p.GetBool("seq_only"))
            {
                return new SuccessResponse("Current sequence",
                    new { seq = NativeEvents.CurrentSeq });
            }

            int sinceSeq = p.GetInt("since_seq") ?? 0;
            var events = NativeEvents.Poll(sinceSeq);
            int currentSeq = NativeEvents.CurrentSeq;

            if (p.GetBool("clear"))
                NativeEvents.Clear();

            var eventsArray = new JArray();
            foreach (var e in events)
            {
                var obj = new JObject
                {
                    ["seq"] = e.seq,
                    ["type"] = e.type,
                    ["ts"] = e.timestampMs,
                };
                // Parse pre-serialized JSON data, or use null
                if (!string.IsNullOrEmpty(e.data))
                {
                    try { obj["data"] = JToken.Parse(e.data); }
                    catch { obj["data"] = e.data; }
                }
                else
                {
                    obj["data"] = null;
                }
                eventsArray.Add(obj);
            }

            return new SuccessResponse($"Polled {events.Count} events", new
            {
                seq = currentSeq,
                events = eventsArray
            });
        }
    }
}
