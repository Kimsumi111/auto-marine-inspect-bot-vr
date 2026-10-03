using UnityEngine;
using UnityEngine.InputSystem;

namespace MetaMarine.VR
{
    // Independent observer rig: never changes the robot's vision/sensor camera.
    [DefaultExecutionOrder(100)]
    public sealed class RobotVrAnchor : MonoBehaviour
    {
        public Transform robot;
        public Transform head;
        public Transform robotCamera;
        public Vector3 eyeOffset = new Vector3(0f, 0.65f, 0.25f);
        private Vector3 trackingZero;
        private Quaternion trackingRotationZero = Quaternion.identity;
        private bool calibrated;
        private InputAction recenter;
        private void OnEnable()
        {
            recenter = new InputAction("Recenter robot view", InputActionType.Button);
            recenter.AddBinding("<XRController>{RightHand}/secondaryButton");
            recenter.AddBinding("<Keyboard>/f9");
            recenter.Enable();
            calibrated = false;
        }
        private void LateUpdate()
        {
            if (robot == null || head == null) return;
            if (recenter.WasPressedThisFrame()) calibrated = false;
            if (!calibrated && UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head)
                    .TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked)
            {
                trackingZero = transform.InverseTransformPoint(head.position);
                trackingRotationZero = Quaternion.Inverse(transform.rotation) * head.rotation;
                calibrated = true;
            }
            // Anchor the neutral HMD pose at the real sensor camera, including its local offset.
            Quaternion cameraRotation = robotCamera != null ? robotCamera.rotation : Quaternion.Euler(0, robot.eulerAngles.y, 0);
            Vector3 cameraPosition = robotCamera != null ? robotCamera.position : robot.position + cameraRotation * eyeOffset;
            Quaternion rigRotation = cameraRotation * Quaternion.Inverse(trackingRotationZero);
            transform.SetPositionAndRotation(cameraPosition - rigRotation * trackingZero, rigRotation);
        }
        private void OnDisable() { recenter?.Dispose(); recenter = null; }
    }
}
