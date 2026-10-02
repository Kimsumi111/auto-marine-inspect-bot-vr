using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace MetaMarine.VR
{
    // Switch only display cameras; robot RenderTexture cameras remain operational.
    public sealed class PcVrView : MonoBehaviour
    {
        public Camera headCamera;
        public AudioListener headListener;
        private readonly List<Camera> cameras = new();
        private readonly List<AudioListener> listeners = new();
        private readonly List<XRDisplaySubsystem> displays = new();
        private bool active;

        private void Awake()
        {
            var robot = GameObject.Find("jetbot");
            if (robot == null || headCamera == null) return;
            var origin = GetComponent<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null)
            {
                origin.CameraYOffset = 0;
                origin.CameraFloorOffsetObject.transform.localPosition = Vector3.zero;
            }
            var anchor = GetComponent<RobotVrAnchor>() ?? gameObject.AddComponent<RobotVrAnchor>();
            anchor.robot = robot.transform;
            anchor.head = headCamera.transform;
            if (GetComponent<VoiceTranscriptPanel>() == null) gameObject.AddComponent<VoiceTranscriptPanel>();
            if (GetComponent<MissionPanel>() == null) gameObject.AddComponent<MissionPanel>();
        }

        private void OnEnable()
        {
            headCamera.enabled = false;
            headListener.enabled = false;
        }

        private void Update()
        {
            SubsystemManager.GetSubsystems(displays);
            bool running = displays.Exists(d => d.running);
            if (running == active) return;
            if (running)
            {
                foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                    if (camera != headCamera && camera.enabled && camera.targetTexture == null)
                    { cameras.Add(camera); camera.enabled = false; }
                foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                    if (listener != headListener && listener.enabled)
                    { listeners.Add(listener); listener.enabled = false; }
                headCamera.enabled = headListener.enabled = active = true;
            }
            else Restore();
        }

        private void OnDisable() => Restore();
        private void Restore()
        {
            if (headCamera != null) headCamera.enabled = false;
            if (headListener != null) headListener.enabled = false;
            foreach (var camera in cameras) if (camera != null) camera.enabled = true;
            foreach (var listener in listeners) if (listener != null) listener.enabled = true;
            cameras.Clear(); listeners.Clear(); active = false;
        }
    }
}
