using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MetaMarine.VR.Editor
{
    [InitializeOnLoad]
    public static class BackendAutoStart
    {
        private const string Preference = "MetaMarine.BackendAutoStart";
        private const string Menu = "Tools/MetaMarine/Auto Start Backend on Play";
        private static UnityWebRequest probe;
        private static double deadline;
        private static bool launched;

        static BackendAutoStart()
        {
            EditorApplication.playModeStateChanged += OnPlay;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeProbe;
        }

        [MenuItem(Menu)]
        private static void Toggle() => EditorPrefs.SetBool(Preference, !EditorPrefs.GetBool(Preference, true));

        [MenuItem(Menu, true)]
        private static bool Validate()
        {
            UnityEditor.Menu.SetChecked(Menu, EditorPrefs.GetBool(Preference, true));
            return true;
        }

        private static void OnPlay(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode || !EditorPrefs.GetBool(Preference, true)) return;
            string scene = SceneManager.GetActiveScene().name;
            if (scene != "jetbot_env" && scene != "EquipmentInspectionDemo") return;
            Start();
        }

        [MenuItem("Tools/MetaMarine/Start Local Backend")]
        public static void Start()
        {
            if (probe != null) return;
            launched = false;
            deadline = EditorApplication.timeSinceStartup + 20;
            Probe();
        }

        private static void Probe()
        {
            probe = UnityWebRequest.Get("http://127.0.0.1:8767/health");
            probe.timeout = 1;
            probe.SendWebRequest();
        }

        private static void Update()
        {
            if (probe == null || !probe.isDone) return;
            bool healthy = probe.result == UnityWebRequest.Result.Success &&
                probe.downloadHandler.text.Contains("metamarine-backend");
            DisposeProbe();
            if (healthy)
            {
                UnityEngine.Debug.Log("MetaMarine Backend ready at http://127.0.0.1:8767 (existing server reused).");
                return;
            }
            if (!launched)
            {
                // Do not start a second process over an occupied or unrelated HTTP service.
                var listener = new TcpListener(IPAddress.Loopback, 8767);
                try { listener.Start(); }
                catch (SocketException)
                {
                    UnityEngine.Debug.LogError("Backend port 8767 is occupied but /health is not ready. Check the existing server.");
                    return;
                }
                finally { listener.Stop(); }
                string root = Directory.GetParent(Application.dataPath).FullName;
                string python = Path.Combine(root, ".venv-backend", "Scripts", "python.exe");
                if (!File.Exists(python)) python = Path.Combine(root, "backend", ".venv", "Scripts", "python.exe");
                if (!File.Exists(python))
                {
                    UnityEngine.Debug.LogError("Backend Python missing. Install the environment using backend/README.md.");
                    return;
                }
                var info = new ProcessStartInfo(python,
                    "-m uvicorn backend.main:app --host 127.0.0.1 --port 8767 --workers 1 --no-access-log")
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
                // The earlier Backend environment also contains the diagnosis dependencies.
                if (!File.Exists(Path.Combine(root, ".venv-diagnosis", "Scripts", "python.exe")) &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHIP_DIAGNOSIS_PYTHON")))
                    info.EnvironmentVariables["SHIP_DIAGNOSIS_PYTHON"] = python;
                try { Process.Start(info)?.Dispose(); }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogError("Backend launch failed: " + exception.GetType().Name);
                    return;
                }
                launched = true;
            }
            if (EditorApplication.timeSinceStartup >= deadline)
            {
                UnityEngine.Debug.LogError("Backend did not become ready within 20 seconds. Check dependencies and backend/README.md.");
                return;
            }
            Probe();
        }

        private static void DisposeProbe()
        {
            probe?.Dispose();
            probe = null;
        }
    }
}
