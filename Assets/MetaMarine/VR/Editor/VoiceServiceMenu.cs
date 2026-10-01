using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MetaMarine.VR.Editor
{
    public static class VoiceServiceMenu
    {
        [MenuItem("Tools/MetaMarine/Start Local Speech Server")]
        public static void Start()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            var python = Path.Combine(root, ".venv-speech", "Scripts", "python.exe");
            if (!File.Exists(python))
            {
                UnityEngine.Debug.LogError("Install tools/voice/requirements.txt in .venv-speech first.");
                return;
            }
            Process.Start(new ProcessStartInfo(python, "\"" + Path.Combine(root, "tools", "voice", "server.py") + "\"")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            UnityEngine.Debug.Log("Local speech server starting at 127.0.0.1:8766. An already-running server stays active.");
        }
    }
}
