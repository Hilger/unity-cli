using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

            // Clean up any stale results
            try { var f = ResultsFilePath(port); if (File.Exists(f)) File.Delete(f); } catch { }

            // Mark pending (survives domain reloads for PlayMode)
            TestRunnerState.MarkPending(port, filter, mode);

            // Signal heartbeat so CLI knows Unity is busy with tests
            Heartbeat.SetTestingState(true);

            var passed  = new List<string>();
            var failed  = new List<string>();
            var skipped = new List<string>();

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new TestCallbacks(
                onResult: r => CollectResult(r, passed, failed, skipped),
                onFinished: _ =>
                {
                    Object.DestroyImmediate(api);
                    TestRunnerState.ClearPending(port);
                    WriteResultsFile(port, passed, failed, skipped);
                    Heartbeat.SetTestingState(false);
                }
            );

            api.RegisterCallbacks(callbacks);
            api.Execute(new ExecutionSettings(BuildFilter(mode, filter)));
        }

        // --- Shared helpers (used by TestRunnerState after domain reload) ---

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
