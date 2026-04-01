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

        // Native data keys — stored in native plugin memory, survives domain reloads.
        // NK_STARTED and NK_RUNID are owned by StartAsyncRun (set once per run).
        // NK_TESTS is a buffer flushed to disk by onFinished, then cleared.
        const string NK_TESTS   = "cli_tests";
        const string NK_STARTED = "cli_started";
        const string NK_RUNID   = "cli_runid";

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
            var assembly = p.Get("assembly", null);
            var runId = p.Get("runId", null);

            StartAsyncRun(testMode, filter, assembly, runId);
            return Task.FromResult<object>(new SuccessResponse("running", new { port = HttpServer.Port }));
        }

        private static void StartAsyncRun(TestMode mode, string filter, string assembly = null, string runId = null)
        {
            int port = HttpServer.Port;

            // Clean up stale data
            try { string f = ResultsFilePath(port); if (File.Exists(f)) File.Delete(f); } catch { }
            NativeData.Delete(NK_TESTS);
            NativeData.SetClear();

            // Set run identity in native memory (survives domain reloads).
            // Only StartAsyncRun writes these — they persist until the next run.
            NativeData.Set(NK_STARTED, DateTime.UtcNow.ToString("o"));
            NativeData.Set(NK_RUNID, runId ?? "");

            // Clear progress and results files
            try
            {
                var projectLogs = ProjectLogsDir();
                Directory.CreateDirectory(projectLogs);
                File.WriteAllText(Path.Combine(projectLogs, "TestProgress.json"), "[]");
                File.WriteAllText(Path.Combine(projectLogs, "TestResults.json"), "");
            }
            catch { }

            TestRunnerState.MarkPending(port, filter, mode);
            Heartbeat.SetTestingState(true);

            TestRunnerApi api = RegisterCallbacksWithNativeAccumulation(port);
            api.Execute(new ExecutionSettings(BuildFilter(mode, filter, assembly)));
        }

        /// <summary>
        /// Creates a TestRunnerApi with callbacks that accumulate results in native memory.
        /// After domain reloads, TestRunnerState calls this WITHOUT Execute to re-register.
        /// </summary>
        internal static TestRunnerApi RegisterCallbacksWithNativeAccumulation(int port)
        {
            if (s_ActiveCallbacks != null)
            {
                TestRunnerApi.UnregisterTestCallback(s_ActiveCallbacks);
                s_ActiveCallbacks = null;
            }

            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            TestCallbacks callbacks = new TestCallbacks(
                onResult: r =>
                {
                    CollectResult(r);
                },
                onFinished: _ =>
                {
                    Object.DestroyImmediate(api);
                    s_ActiveCallbacks = null;
                    TestRunnerState.ClearPending(port);
                    FlushResultsToFile(port);
                    Heartbeat.SetTestingState(false);
                }
            );

            s_ActiveCallbacks = callbacks;
            api.RegisterCallbacks(callbacks);
            return api;
        }

        static TestCallbacks s_ActiveCallbacks;

        // --- Per-test result handling ---

        /// <summary>
        /// Called for each test result. Stores in native memory buffer and
        /// appends to the progress file (append-only).
        /// </summary>
        static void CollectResult(ITestResultAdaptor result)
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

            // Deduplicate: Unity has a recurring bug where TestFinished fires
            // twice for the same test after domain reloads (fixed in v1.4.5,
            // v1.5.1, but has regressed multiple times historically).
            // See: https://docs.unity3d.com/Packages/com.unity.test-framework@1.5/changelog/CHANGELOG.html#151---2025-02-26
            if (!NativeData.SetAdd(result.Test.FullName))
                return;

            // Accumulate in native memory (survives domain reloads)
            var tests = LoadNativeTests();
            tests.Add(entry);
            NativeData.Set(NK_TESTS, tests.ToString(Formatting.None));

            // Append to progress file (append-only, CLI polls this for progress)
            AppendProgress(entry);
        }

        /// <summary>
        /// Appends a single test entry to Logs/TestProgress.json.
        /// The file is a JSON array; we read, append, write back.
        /// </summary>
        static void AppendProgress(JObject entry)
        {
            try
            {
                var path = Path.Combine(ProjectLogsDir(), "TestProgress.json");
                JArray arr;
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path);
                    arr = string.IsNullOrEmpty(existing) ? new JArray() : JArray.Parse(existing);
                }
                else
                {
                    arr = new JArray();
                }
                arr.Add(entry);
                File.WriteAllText(path, arr.ToString(Formatting.None));
            }
            catch { }
        }

        static JArray LoadNativeTests()
        {
            var json = NativeData.Get(NK_TESTS) ?? "[]";
            try { return JArray.Parse(json); }
            catch { return new JArray(); }
        }

        // --- Final results (written once by onFinished) ---

        /// <summary>
        /// Flushes accumulated test results from native memory to disk.
        /// Reads cli_tests, writes to TestResults.json and the legacy CLI polling file,
        /// then clears cli_tests. Run identity (cli_runid, cli_started) is NOT cleared —
        /// it persists until the next StartAsyncRun.
        /// </summary>
        static void FlushResultsToFile(int port)
        {
            var tests = LoadNativeTests();
            var startedStr = NativeData.Get(NK_STARTED);
            var runId = NativeData.Get(NK_RUNID);

            // If cli_tests is empty, nothing to flush (already flushed or no tests ran).
            // Don't overwrite the results file with empty data.
            if (tests.Count == 0 && !string.IsNullOrEmpty(runId))
            {
                // Check if results file already has data for this run
                try
                {
                    var existingPath = Path.Combine(ProjectLogsDir(), "TestResults.json");
                    if (File.Exists(existingPath))
                    {
                        var existing = File.ReadAllText(existingPath);
                        if (!string.IsNullOrEmpty(existing))
                            return; // Already written, don't overwrite
                    }
                }
                catch { }
            }

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

            // Legacy CLI polling file
            var cliData = new
            {
                success = failed == 0,
                message = failed > 0
                    ? $"{failed} test(s) failed."
                    : $"All {passed} test(s) passed.",
                data = new
                {
                    total = tests.Count,
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

            // Project-local results (CLI polls this with runId matching)
            var projectData = new JObject
            {
                ["status"]      = "completed",
                ["startedAt"]   = startedStr,
                ["completedAt"] = DateTime.UtcNow.ToString("o"),
                ["runId"]       = runId,
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
                var projectLogs = ProjectLogsDir();
                Directory.CreateDirectory(projectLogs);
                File.WriteAllText(
                    Path.Combine(projectLogs, "TestResults.json"),
                    projectData.ToString(Formatting.Indented));
            }
            catch { }

            // Clear the test buffer (run identity persists until next StartAsyncRun)
            NativeData.Delete(NK_TESTS);
        }

        internal static string ResultsFilePath(int port) =>
            Path.Combine(StatusDir, $"test-results-{port}.json");

        static string ProjectLogsDir() =>
            Path.Combine(Application.dataPath, "..", "Logs");

        internal static Filter BuildFilter(TestMode mode, string filterStr, string assemblyStr = null)
        {
            var f = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(filterStr))
            {
                f.groupNames = new[] { filterStr };
            }
            if (!string.IsNullOrEmpty(assemblyStr))
            {
                var names = assemblyStr.Split(',');
                for (int i = 0; i < names.Length; i++)
                    names[i] = names[i].Trim();
                f.assemblyNames = names;
                Debug.Log($"[UnityCliConnector] Assembly filter set: [{string.Join(", ", f.assemblyNames)}]");
            }
            else
            {
                Debug.Log("[UnityCliConnector] No assembly filter — running all tests");
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
