using UnityEngine;

namespace ShipRobot.Navigation
{
    // Ideal simulation input. No UWB radio, ranging or NFC hardware is implemented.
    [DisallowMultipleComponent]
    public sealed class SimulatedIndoorSensors : MonoBehaviour
    {
        [SerializeField] private Transform reader;
        [SerializeField, Min(0.01f)] private float positionUpdateInterval = 0.10f;
        [SerializeField, Min(0.1f)] private float maximumPositionAge = 0.50f;
        private Vector3 sampledPosition;
        private float sampleTime = -1f;
        public Transform ReaderTransform => reader != null ? reader : transform;
        public Vector3 LastSampledPosition => sampledPosition;
        public float LastSampleTime => sampleTime;
        public bool HasFreshPosition => isActiveAndEnabled && sampleTime >= 0f && Time.time - sampleTime <= maximumPositionAge;
        private BoxCollider bodyCollider;
        private Rigidbody body;
        public string PositionBasis => reader != null ? "explicit_reader" :
            bodyCollider != null ? "box_collider_centre" : body != null ? "rigidbody_centre_of_mass" : "transform_fallback";
        public Vector3 ReaderPosition => reader != null ? reader.position :
            bodyCollider != null ? bodyCollider.transform.TransformPoint(bodyCollider.center) :
            body != null ? body.worldCenterOfMass : transform.position;
        // Heading is a separate ideal orientation input, NOT a UWB measurement.
        public Vector3 HeadingForward => transform.forward;

        private void OnEnable()
        {
            bodyCollider = GetComponent<BoxCollider>();
            body = GetComponent<Rigidbody>();
            sampleTime = -1f;
        }
        private void Update() => SamplePosition();
        private void SamplePosition()
        {
            if (!isActiveAndEnabled) return;
            if (sampleTime >= 0f && Time.time - sampleTime < positionUpdateInterval) return;
            sampledPosition = ReaderPosition;
            sampleTime = Time.time;
        }
        public bool TryGetPosition(out Vector3 position)
        {
            SamplePosition();
            position = sampledPosition;
            return isActiveAndEnabled && sampleTime >= 0f && Time.time - sampleTime <= maximumPositionAge;
        }
        public bool IsInside(NavigationMarker zone)
        {
            if (!isActiveAndEnabled || zone == null || !zone.isActiveAndEnabled) return false;
            Vector3 d = ReaderPosition - zone.transform.position;
            // Floor-plan zones; tag height does not represent radio reading distance.
            return IndoorProximity.Contains(d.x, d.z, zone.ProximityRadius);
        }
    }
}
