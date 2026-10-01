using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine.XR.OpenXR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR.Features.Interactions;
using Unity.XR.CoreUtils;

namespace MetaMarine.VR.Editor
{
    public static class PcVrSetup
    {
        [MenuItem("Tools/MetaMarine/Setup PC VR View")]
        public static void Setup()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Open a saved scene first.");

            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget settings);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(settings, "Assets/XR/PCVRGeneralSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, settings, true);
            }
            const BuildTargetGroup group = BuildTargetGroup.Standalone;
            if (!settings.HasManagerSettingsForBuildTarget(group)) settings.CreateDefaultManagerSettingsForBuildTarget(group);
            var general = settings.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            if (!XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group))
                throw new InvalidOperationException("Could not assign OpenXR loader.");
            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            var touch = openxr.GetFeature<OculusTouchControllerProfile>();
            if (touch == null) throw new InvalidOperationException("Oculus Touch profile is missing.");
            touch.enabled = true;
            EditorUtility.SetDirty(touch); EditorUtility.SetDirty(openxr);
            EditorUtility.SetDirty(general.Manager); EditorUtility.SetDirty(general); EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            if (UnityEngine.Object.FindAnyObjectByType<PcVrView>() == null)
            {
                var root = new GameObject("PC VR Observer");
                Undo.RegisterCreatedObjectUndo(root, "Add PC VR Observer");
                var robot = GameObject.Find("jetbot");
                root.transform.position = robot != null
                    ? robot.transform.position - robot.transform.forward * 2f + robot.transform.right * 2f
                    : Vector3.zero;
                if (robot != null) root.transform.rotation = Quaternion.Euler(0, robot.transform.eulerAngles.y, 0);
                var offset = new GameObject("Camera Offset");
                offset.transform.SetParent(root.transform, false);
                var head = new GameObject("VR Head Camera");
                head.transform.SetParent(offset.transform, false);
                var camera = head.AddComponent<Camera>();
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 300f;
                camera.stereoTargetEye = StereoTargetEyeMask.Both;
                camera.enabled = false;
                var listener = head.AddComponent<AudioListener>();
                listener.enabled = false;
                var driver = head.AddComponent<TrackedPoseDriver>();
                driver.positionInput = new InputActionProperty(new InputAction("Head Position", InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
                driver.rotationInput = new InputActionProperty(new InputAction("Head Rotation", InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
                driver.trackingStateInput = new InputActionProperty(new InputAction("Head Tracking", InputActionType.Value, "<XRHMD>/trackingState", expectedControlType: "Integer"));
                var origin = root.AddComponent<XROrigin>();
                origin.Camera = camera;
                origin.CameraFloorOffsetObject = offset;
                origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
                origin.CameraYOffset = 1.6f;
                var view = root.AddComponent<PcVrView>();
                view.headCamera = camera; view.headListener = listener;
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(scene);
            }
            var vr = UnityEngine.Object.FindAnyObjectByType<PcVrView>();
            var target = GameObject.Find("jetbot");
            if (target == null) throw new InvalidOperationException("Robot jetbot was not found.");
            var xrOrigin = vr.GetComponent<XROrigin>();
            xrOrigin.CameraYOffset = 0;
            xrOrigin.CameraFloorOffsetObject.transform.localPosition = Vector3.zero;
            var anchor = vr.GetComponent<RobotVrAnchor>() ?? vr.gameObject.AddComponent<RobotVrAnchor>();
            anchor.robot = target.transform;
            anchor.head = vr.headCamera.transform;
            var speech = vr.GetComponent<VoiceTranscriptPanel>() ?? vr.gameObject.AddComponent<VoiceTranscriptPanel>();
            speech.headCamera = vr.headCamera;
            EditorUtility.SetDirty(xrOrigin); EditorUtility.SetDirty(anchor); EditorUtility.SetDirty(speech);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("MetaMarine PC VR setup complete. Connect Quest Link, then press Play. Observer position is editable; desktop cameras remain active without an XR display.");
        }
    }
}
