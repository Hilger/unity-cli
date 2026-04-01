using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.TestRunner
{
    /// <summary>
    /// Survives domain reloads via [InitializeOnLoad].
    /// After each domain reload, checks for pending test runs and re-registers
    /// callbacks so RunFinished fires and results are written to file.
    /// Results are accumulated in SessionState across reloads.
    /// </summary>
    [InitializeOnLoad]
    public static class TestRunnerState
    {
        static TestRunnerState()
        {
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static void MarkPending(int port, string filter, UnityEditor.TestTools.TestRunner.Api.TestMode mode)
        {
            var pending = new { port, filter = filter ?? "", mode = mode.ToString() };
            try
            {
                Directory.CreateDirectory(RunTests.StatusDir);
                File.WriteAllText(PendingFilePath(port), JsonConvert.SerializeObject(pending));
            }
            catch { }
        }

        public static void ClearPending(int port)
        {
            try
            {
                var path = PendingFilePath(port);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        static void OnAfterAssemblyReload()
        {
            try
            {
                if (!Directory.Exists(RunTests.StatusDir)) return;

                foreach (var file in Directory.GetFiles(RunTests.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var pending = JObject.Parse(json);
                    var port = pending["port"]?.Value<int>() ?? 0;

                    if (port == 0) continue;

                    // Restore heartbeat testing state (s_Testing was lost during reload)
                    Heartbeat.SetTestingState(true);

                    // Re-register callbacks using native data accumulation
                    // Results collected before this reload are in native memory
                    RunTests.RegisterCallbacksWithNativeAccumulation(port);
                }
            }
            catch { }
        }

        static string PendingFilePath(int port) =>
            Path.Combine(RunTests.StatusDir, $"test-pending-{port}.json");
    }
}
