using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    [InitializeOnLoad]
    public static class Heartbeat
    {
        static readonly string s_Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "instances");

        static double s_LastWrite;
        const double INTERVAL = 0.5;
        static string s_ForcedState;
        static double s_CompileRequestTime;
        static string s_FilePath;
        static bool s_Testing;

        static Heartbeat()
        {
            EditorApplication.update += Tick;
            EditorApplication.quitting += Cleanup;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnAfterAssemblyReload()
        {
            s_LastWrite = 0;
            // Check if tests were running before reload (pending file exists)
            try
            {
                var statusDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "status");
                if (Directory.Exists(statusDir) &&
                    Directory.GetFiles(statusDir, "test-pending-*.json").Length > 0)
                {
                    s_Testing = true;
                    s_ForcedState = "testing";
                    return;
                }
            }
            catch { }

            s_ForcedState = null;
        }

        static void OnBeforeAssemblyReload()
        {
            // Preserve testing state across reload by writing "testing" not "reloading"
            WriteState(s_Testing ? "testing" : "reloading");
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                WriteState("entering_playmode");
        }

        static void WriteState(string state)
        {
            s_ForcedState = state;
            Write();
        }

        /// <summary>
        /// Sets or clears the "testing" state. While true, heartbeat reports "testing"
        /// so the CLI knows Unity is busy running tests (not unresponsive).
        /// </summary>
        public static void SetTestingState(bool testing)
        {
            s_Testing = testing;
            if (testing)
                WriteState("testing");
            else
            {
                s_ForcedState = null;
                Write();
            }
        }

        /// <summary>
        /// Marks that a compile was requested. Keeps "compiling" state forced
        /// for a grace period so the CLI poller never sees a premature "ready".
        /// </summary>
        public static void MarkCompileRequested()
        {
            s_CompileRequestTime = EditorApplication.timeSinceStartup;
            WriteState("compiling");
        }

        static void Tick()
        {
            if (HttpServer.Port == 0) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastWrite < INTERVAL) return;
            s_LastWrite = now;

            if (s_CompileRequestTime > 0)
            {
                if (now - s_CompileRequestTime < 3.0 && EditorApplication.isCompiling == false)
                {
                    Write();
                    return;
                }
                s_CompileRequestTime = 0;
            }

            if (!s_Testing) s_ForcedState = null;
            Write();
        }

        static string GetFilePath()
        {
            if (s_FilePath != null) return s_FilePath;
            var projectPath = Application.dataPath.Replace("/Assets", "");
            using var md5 = MD5.Create();
            var hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(projectPath)))
                .Replace("-", "").Substring(0, 16).ToLower();
            s_FilePath = Path.Combine(s_Dir, $"{hash}.json");
            return s_FilePath;
        }

        static void Write()
        {
            var status = new
            {
                state = s_ForcedState ?? GetState(),
                projectPath = Application.dataPath.Replace("/Assets", ""),
                port = HttpServer.Port,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                unityVersion = Application.unityVersion,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                compileErrors = EditorUtility.scriptCompilationFailed,
            };

            try
            {
                Directory.CreateDirectory(s_Dir);
                File.WriteAllText(GetFilePath(), JsonConvert.SerializeObject(status));
            }
            catch
            {
            }
        }

        static string GetState()
        {
            if (s_Testing) return "testing";
            if (EditorApplication.isCompiling) return "compiling";
            if (EditorApplication.isUpdating) return "refreshing";
            if (EditorApplication.isPlaying)
                return EditorApplication.isPaused ? "paused" : "playing";
            return "ready";
        }

        public static void Cleanup()
        {
            if (HttpServer.Port == 0) return;
            s_ForcedState = "stopped";
            Write();
        }
    }
}
