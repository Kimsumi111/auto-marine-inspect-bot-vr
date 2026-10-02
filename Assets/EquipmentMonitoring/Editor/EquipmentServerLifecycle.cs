using UnityEditor;
using UnityEngine;

namespace ShipRobot.EquipmentMonitoring.Editor
{
    [InitializeOnLoad]
    public static class EquipmentServerLifecycle
    {
        static EquipmentServerLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += EquipmentTcpServer.DisposeAll;
            EditorApplication.quitting += EquipmentTcpServer.DisposeAll;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) EquipmentTcpServer.DisposeAll();
                if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Restore;
            };
            // A reload during Play disposes sockets but leaves enabled components in the scene.
            EditorApplication.delayCall += Restore;
        }

        [MenuItem("Tools/Ship Robot/Check and Restore Equipment TCP")]
        private static void Restore()
        {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            var bridges = Object.FindObjectsByType<EquipmentNetworkBridge>(FindObjectsSortMode.None);
            foreach (var bridge in bridges)
            {
                bridge.EnsureServerStarted();
                Debug.Log($"Equipment TCP status: {bridge.Status}, object={bridge.name}, enabled={bridge.isActiveAndEnabled}, mission={(bridge.MissionSnapshot != null)}", bridge);
            }
            if (bridges.Length == 0)
            {
                var nav = Object.FindAnyObjectByType<ShipRobot.Navigation.NavigationCoordinator>();
                Debug.LogWarning($"Equipment TCP bridge missing: navigation={(nav != null)}, training={(nav != null && nav.IsTrainingMission)}. Check scene and simulation mode.");
            }
        }
    }
}
