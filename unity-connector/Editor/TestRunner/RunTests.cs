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
        const string SK_PASSED  = "UnityCliConnector_TestPassed";
        const string SK_FAILED  = "UnityCliConnector_TestFailed";
        const string SK_SKIPPED = "UnityCliConnector_TestSkipped";

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
            // This prevents EditMode tests from blocking the HTTP response (which caused
            // "connection closed before response" when tests took longer than the CLI timeout).
            StartAsyncRun(testMode, filter);
            return Task.FromResult<object>(new SuccessResponse("running", new { port = HttpServer.Port }));
        }

        private static void StartAsyncRun(TestMode mode, string filter)
        {
            var port = HttpServer.Port;

            // Clean up stale results and session state
            try { var f = ResultsFilePath(port); if (File.Exists(f)) File.Delete(f); } catch { }
            ClearSessionResults();

            // Mark pending (survives domain reloads)
            TestRunnerState.MarkPending(port, filter, mode);

            // Signal heartbeat so CLI knows Unity is busy with tests
            Heartbeat.SetTestingState(true);

            RegisterCallbacksWithSessionAccumulation(port);
        }

        /// <summary>
        /// Registers test callbacks that accumulate results in SessionState.
        /// SessionState survives domain reloads within a Unity session, so results
        /// from tests that ran before a reload are preserved.
        /// </summary>
        internal static void RegisterCallbacksWithSessionAccumulation(int port)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new TestCallbacks(
                onResult: r => CollectResultToSession(r),
                onFinished: _ =>
                {
                    Object.DestroyImmediate(api);

                    // Read accumulated results from SessionState
                    var passed  = LoadSessionList(SK_PASSED);
                    var failed  = LoadSessionList(SK_FAILED);
                    var skipped = LoadSessionList(SK_SKIPPED);

                    TestRunnerState.ClearPending(port);
                    ClearSessionResults();
                    WriteResultsFile(port, passed, failed, skipped);
                    Heartbeat.SetTestingState(false);
                }
            );

            api.RegisterCallbacks(callbacks);
        }

        // --- SessionState accumulation (survives domain reloads) ---

        static void CollectResultToSession(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite) return;
            var name = result.Test.FullName;
            switch (result.TestStatus)
            {
                case TestStatus.Passed:
                    AppendToSessionList(SK_PASSED, name);
                    break;
                case TestStatus.Failed:
                    AppendToSessionList(SK_FAILED, $"{name}: {result.Message}");
                    break;
                default:
                    AppendToSessionList(SK_SKIPPED, name);
                    break;
            }
        }

        static void AppendToSessionList(string key, string value)
        {
            var list = LoadSessionList(key);
            list.Add(value);
            SessionState.SetString(key, JsonConvert.SerializeObject(list));
        }

        internal static List<string> LoadSessionList(string key)
        {
            var json = SessionState.GetString(key, "[]");
            try { return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        static void ClearSessionResults()
        {
            SessionState.EraseString(SK_PASSED);
            SessionState.EraseString(SK_FAILED);
            SessionState.EraseString(SK_SKIPPED);
        }

        // --- Shared helpers ---

        internal static void CollectResult(ITestResultAdaptor result,
            List<string> passed, List<string> failed, List<string> skipped)
        {
            if (result.Test.IsSuite) return;
            var name = result.Test.FullName;
            switch (result.TestStatus)
            {
                case TestStatus.Passed:  passed.Add(name); break;
                case TestStatus.Failed:  failed.Add($"{name}: {result.Message}"); break;
                default:                 skipped.Add(name); break;
            }
        }

        internal static void WriteResultsFile(int port, List<string> passed, List<string> failed, List<string> skipped)
        {
            var json = JsonConvert.SerializeObject(BuildResultsData(passed, failed, skipped), Formatting.Indented);

            // Global status dir (for CLI polling)
            try
            {
                Directory.CreateDirectory(StatusDir);
                File.WriteAllText(ResultsFilePath(port), json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliConnector] Failed to write test results: {ex.Message}");
            }

            // Project-local (for tools like Claude Code to read directly via file path)
            try
            {
                var projectLogs = Path.Combine(Application.dataPath, "..", "Logs");
                Directory.CreateDirectory(projectLogs);
                File.WriteAllText(Path.Combine(projectLogs, "TestResults.json"), json);
            }
            catch { }
        }

        internal static object BuildResultsData(List<string> passed, List<string> failed, List<string> skipped)
        {
            return new
            {
                success = failed.Count == 0,
                message = failed.Count > 0
                    ? $"{failed.Count} test(s) failed."
                    : $"All {passed.Count} test(s) passed.",
                data = new
                {
                    total   = passed.Count + failed.Count + skipped.Count,
                    passed  = passed.Count,
                    failed  = failed.Count,
                    skipped = skipped.Count,
                    failures = failed,
                    passes   = passed,
                }
            };
        }

        internal static string ResultsFilePath(int port) =>
            Path.Combine(StatusDir, $"test-results-{port}.json");

        internal static object BuildResponse(List<string> passed, List<string> failed, List<string> skipped)
        {
            var summary = new
            {
                total   = passed.Count + failed.Count + skipped.Count,
                passed  = passed.Count,
                failed  = failed.Count,
                skipped = skipped.Count,
                failures = failed,
                passes   = passed,
            };
            return failed.Count > 0
                ? (object)new ErrorResponse($"{failed.Count} test(s) failed.", summary)
                : new SuccessResponse($"All {passed.Count} test(s) passed.", summary);
        }

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
