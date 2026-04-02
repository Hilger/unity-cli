using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Hooks into Unity editor callbacks and pushes typed events
    /// into the native event ring buffer and log ring buffer.
    /// </summary>
    [InitializeOnLoad]
    public static class EventPublisher
    {
        static string s_LastState;

        static EventPublisher()
        {
            Application.logMessageReceivedThreaded += OnLogMessage;
            CompilationPipeline.compilationStarted += _ => OnCompileStart();
            CompilationPipeline.compilationFinished += _ => OnCompileEnd();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += CheckStateChange;
        }

        static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            // Push full log entry to dedicated log ring buffer
            int nativeType = type switch
            {
                LogType.Log       => 0,
                LogType.Warning   => 1,
                LogType.Error     => 2,
                LogType.Exception => 3,
                LogType.Assert    => 4,
                _                 => 0
            };
            NativeLogs.Push(nativeType, message, stackTrace);

            // Also push truncated version to general event stream
            var data = JsonConvert.SerializeObject(new
            {
                logType = (int)type,
                message = Truncate(message, 2048),
                stack = Truncate(stackTrace, 1024)
            });
            NativeEvents.Push(NativeEvents.EventType.LogMessage, data);
        }

        static void OnCompileStart()
        {
            NativeEvents.Push(NativeEvents.EventType.CompileStart,
                "{\"state\":\"compiling\"}");
        }

        static void OnCompileEnd()
        {
            var data = JsonConvert.SerializeObject(new
            {
                errors = EditorUtility.scriptCompilationFailed
            });
            NativeEvents.Push(NativeEvents.EventType.CompileEnd, data);
        }

        static void OnBeforeReload()
        {
            NativeEvents.Push(NativeEvents.EventType.DomainReload,
                "{\"phase\":\"before\"}");
        }

        static void OnAfterReload()
        {
            NativeEvents.Push(NativeEvents.EventType.DomainReload,
                "{\"phase\":\"after\"}");
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            var data = JsonConvert.SerializeObject(new
            {
                change = change.ToString()
            });
            NativeEvents.Push(NativeEvents.EventType.PlayMode, data);
        }

        static void CheckStateChange()
        {
            string state = GetCurrentState();
            if (state != s_LastState)
            {
                s_LastState = state;
                NativeEvents.Push(NativeEvents.EventType.StateChange,
                    JsonConvert.SerializeObject(new { state }));
            }
        }

        static string GetCurrentState()
        {
            if (EditorApplication.isCompiling) return "compiling";
            if (EditorApplication.isUpdating) return "refreshing";
            if (EditorApplication.isPlaying)
                return EditorApplication.isPaused ? "paused" : "playing";
            return "ready";
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
