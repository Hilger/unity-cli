using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCliConnector.TestRunner
{
    [UnityCliTool(Description = "Run Unity EditMode or PlayMode tests and return results.")]
    public static class RunTests
    {
        internal static readonly string StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "status");

        // SessionState keys — survive domain reloads within a Unity session
        const string SK_TESTS     = "UnityCliConnector_Tests";      // JSON array of test entry objects
        const string SK_STARTED   = "UnityCliConnector_TestStarted"; // ISO timestamp

        public class Parameters
        {
            [ToolParameter("Test mode: EditMode or PlayMode", Required = true)]
            public string Mode { get; set; }

            [ToolParameter("Filter by namespace, class, or full test name")]
            public string Filter { get; set; }
        }

        public static Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return Task.FromResult<object>(new ErrorResponse("Parameters cannot be null."));

            var p = new ToolParams(@params);

            var modeResult = p.GetRequired("mode");
            if (!modeResult.IsSuccess)
                return Task.FromResult<object>(new ErrorResponse(modeResult.ErrorMessage));

            var modeStr = modeResult.Value.Trim();
            TestMode testMode;
            if (modeStr.Equals("EditMode", StringComparison.OrdinalIgnoreCase))
                testMode = TestMode.EditMode;
            else if (modeStr.Equals("PlayMode", StringComparison.OrdinalIgnoreCase))
                testMode = TestMode.PlayMode;
            else
                return Task.FromResult<object>(new ErrorResponse($"Unknown mode '{modeStr}'. Use EditMode or PlayMode."));

            var filter = p.Get("filter", null);

            // Both modes use fire-and-forget: return immediately, write results to file.
            StartAsyncRun(testMode, filter);
            return Task.FromResult<object>(new SuccessResponse("running", new { port = HttpServer.Port }));
        }

        private static void StartAsyncRun(TestMode mode, string filter)
        {
            var port = HttpServer.Port;

            // Clean up stale results and session state
            try { var f = ResultsFilePath(port); if (File.Exists(f)) File.Delete(f); } catch { }
            ClearSessionState();

            // Record start time
            SessionState.SetString(SK_STARTED, DateTime.UtcNow.ToString("o"));

            // Mark pending (survives domain reloads)
            TestRunnerState.MarkPending(port, filter, mode);

            // Signal heartbeat
            Heartbeat.SetTestingState(true);

            // Write initial progress file
            WriteProgressFile("running");

            RegisterCallbacksWithSessionAccumulation(port);
        }

        /// <summary>
        /// Registers test callbacks that accumulate results in SessionState
        /// and write incremental progress to Logs/TestResults.json after each test.
        /// </summary>
        internal static void RegisterCallbacksWithSessionAccumulation(int port)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new TestCallbacks(
                onResult: r =>
                {
                    CollectResultToSession(r);
                    WriteProgressFile("running");
                },
                onFinished: _ =>
                {
                    Object.DestroyImmediate(api);
                    TestRunnerState.ClearPending(port);
                    WriteFinalResults(port);
                    ClearSessionState();
                    Heartbeat.SetTestingState(false);
                }
            );

            api.RegisterCallbacks(callbacks);
        }

        // --- SessionState accumulation (survives domain reloads) ---

        static void CollectResultToSession(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite) return;

            var entry = new JObject
            {
                ["name"]     = result.Test.FullName,
                ["status"]   = result.TestStatus.ToString().ToLower(),
                ["duration"] = Math.Round(result.Duration, 4)
            };
            if (result.TestStatus == TestStatus.Failed)
                entry["message"] = result.Message ?? "";

            var tests = LoadSessionTests();
            tests.Add(entry);
            SessionState.SetString(SK_TESTS, tests.ToString(Formatting.None));
        }

        static JArray LoadSessionTests()
        {
            var json = SessionState.GetString(SK_TESTS, "[]");
            try { return JArray.Parse(json); }
            catch { return new JArray(); }
        }

        static void ClearSessionState()
        {
            SessionState.EraseString(SK_TESTS);
            SessionState.EraseString(SK_STARTED);
        }

        // --- File output ---

        /// <summary>
        /// Writes incremental progress to the project-local Logs/TestResults.json.
        /// Called after each test completes so external tools can read mid-run.
        /// </summary>
        static void WriteProgressFile(string status)
        {
            var tests = LoadSessionTests();
            var startedStr = SessionState.GetString(SK_STARTED, null);

            int passed = 0, failed = 0, skipped = 0;
            foreach (var t in tests)
            {
                var s = t["status"]?.ToString();
                if (s == "passed") passed++;
                else if (s == "failed") failed++;
                else skipped++;
            }

            var data = new JObject
            {
                ["status"]    = status,
                ["startedAt"] = startedStr,
                ["summary"] = new JObject
                {
                    ["passed"]  = passed,
                    ["failed"]  = failed,
                    ["skipped"] = skipped,
                    ["total"]   = tests.Count
                },
                ["tests"] = tests
            };

            try
            {
                var projectLogs = Path.Combine(Application.dataPath, "..", "Logs");
                Directory.CreateDirectory(projectLogs);
                File.WriteAllText(
                    Path.Combine(projectLogs, "TestResults.json"),
                    data.ToString(Formatting.Indented));
            }
            catch { }
        }

        /// <summary>
        /// Writes final results to both the CLI polling path and the project-local path.
        /// </summary>
        static void WriteFinalResults(int port)
        {
            var tests = LoadSessionTests();
            var startedStr = SessionState.GetString(SK_STARTED, null);

            int passed = 0, failed = 0, skipped = 0;
            var failures = new List<string>();
            var passes = new List<string>();

            foreach (var t in tests)
            {
                var name = t["name"]?.ToString() ?? "";
                var s = t["status"]?.ToString();
                if (s == "passed") { passed++; passes.Add(name); }
                else if (s == "failed") { failed++; failures.Add($"{name}: {t["message"]}"); }
                else skipped++;
            }

            // CLI polling file (legacy format expected by Go CLI)
            var cliData = new
            {
                success = failed == 0,
                message = failed > 0
                    ? $"{failed} test(s) failed."
                    : $"All {passed} test(s) passed.",
                data = new
                {
                    total    = tests.Count,
                    passed,
                    failed,
                    skipped,
                    failures,
                    passes,
                }
            };

            try
            {
                Directory.CreateDirectory(StatusDir);
                File.WriteAllText(ResultsFilePath(port),
                    JsonConvert.SerializeObject(cliData));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliConnector] Failed to write test results: {ex.Message}");
            }

            // Project-local: full granular format with per-test entries
            var projectData = new JObject
            {
                ["status"]      = "completed",
                ["startedAt"]   = startedStr,
                ["completedAt"] = DateTime.UtcNow.ToString("o"),
                ["summary"] = new JObject
                {
                    ["passed"]  = passed,
                    ["failed"]  = failed,
                    ["skipped"] = skipped,
                    ["total"]   = tests.Count
                },
                ["tests"] = tests
            };

            try
            {
                var projectLogs = Path.Combine(Application.dataPath, "..", "Logs");
                Directory.CreateDirectory(projectLogs);
                File.WriteAllText(
                    Path.Combine(projectLogs, "TestResults.json"),
                    projectData.ToString(Formatting.Indented));
            }
            catch { }
        }

        internal static string ResultsFilePath(int port) =>
            Path.Combine(StatusDir, $"test-results-{port}.json");

        internal static Filter BuildFilter(TestMode mode, string filterStr)
        {
            var f = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(filterStr))
            {
                f.testNames  = new[] { filterStr };
                f.groupNames = new[] { filterStr };
            }
            return f;
        }

        internal class TestCallbacks : ICallbacks
        {
            private readonly Action<ITestResultAdaptor> _onResult;
            private readonly Action<ITestResultAdaptor> _onFinished;

            public TestCallbacks(Action<ITestResultAdaptor> onResult, Action<ITestResultAdaptor> onFinished)
            {
                _onResult   = onResult;
                _onFinished = onFinished;
            }

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) => _onFinished(result);
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) => _onResult(result);
        }
    }
}
