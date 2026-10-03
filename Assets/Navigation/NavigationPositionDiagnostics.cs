using System;
using UnityEngine;

namespace ShipRobot.Navigation
{
    // Read-only snapshot: does not sample sensors or apply physics commands.
    public static class NavigationPositionDiagnostics
    {
        [Serializable] private sealed class Point
        {
            public string path;
            public Vector3 world, local, scale;
            public Point(Transform t) { path = Path(t); world = t.position; local = t.localPosition; scale = t.lossyScale; }
        }
        [Serializable] private sealed class Wheel
        {
            public string path;
            public Vector3 centre, visualPosition, contact;
            public bool grounded;
            public float rpm, torque, brake, sidewaysSlip, forwardSlip;
        }
        [Serializable] private sealed class Zone
        {
            public string path, role;
            public int node;
            public Vector3 position, readerDelta;
            public float radius, readerDistance, originDistance;
            public bool enabled, inside;
        }
        [Serializable] private sealed class Snapshot
        {
            public int version = 2, frame;
            public string positionBasis;
            public Vector3 readerOffsetFromOrigin;
            public float realtime, simulationTime, timeScale;
            public Point origin, reader;
            public bool sensorEnabled, uwbFresh, hasRigidbody, hasCollider;
            public Vector3 uwbPosition, centreOfMass, rigidbodyPosition, velocity, angularVelocity, colliderCentre, wheelMean;
            public float uwbTimestamp, uwbAge;
            public Zone entry, centre;
            public Wheel[] wheels;
        }
        private static string Path(Transform t)
        {
            string s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
        private static Zone DescribeZone(NavigationMarker z, Transform robot, SimulatedIndoorSensors sensor)
        {
            if (z == null) return null;
            Vector3 reader = sensor != null ? sensor.ReaderPosition : robot.position;
            Vector3 d = reader - z.transform.position;
            Vector3 o = robot.position - z.transform.position;
            return new Zone { path = Path(z.transform), role = z.Role.ToString(), node = (int)z.NodeId,
                position = z.transform.position, readerDelta = d, radius = z.ProximityRadius,
                readerDistance = new Vector2(d.x, d.z).magnitude, originDistance = new Vector2(o.x, o.z).magnitude,
                enabled = z.isActiveAndEnabled, inside = sensor != null && sensor.IsInside(z) };
        }
        public static string Capture(Transform robot, SimulatedIndoorSensors sensor, NavigationMarker entry, NavigationMarker centre)
        {
            var s = new Snapshot { frame = Time.frameCount, realtime = Time.realtimeSinceStartup,
                simulationTime = Time.time, timeScale = Time.timeScale, origin = new Point(robot),
                sensorEnabled = sensor != null && sensor.isActiveAndEnabled,
                entry = DescribeZone(entry, robot, sensor), centre = DescribeZone(centre, robot, sensor) };
            if (sensor != null)
            {
                s.reader = new Point(sensor.ReaderTransform);
                s.reader.world = sensor.ReaderPosition;
                s.reader.local = sensor.ReaderTransform.parent != null
                    ? sensor.ReaderTransform.parent.InverseTransformPoint(sensor.ReaderPosition) : sensor.ReaderPosition;
                s.positionBasis = sensor.PositionBasis;
                s.readerOffsetFromOrigin = robot.InverseTransformPoint(sensor.ReaderPosition);
                s.uwbPosition = sensor.LastSampledPosition;
                s.uwbTimestamp = sensor.LastSampleTime; s.uwbAge = Time.time - s.uwbTimestamp; s.uwbFresh = sensor.HasFreshPosition;
            }
            Rigidbody body = robot.GetComponent<Rigidbody>();
            s.hasRigidbody = body != null;
            if (body != null)
            {
                s.centreOfMass = body.worldCenterOfMass; s.rigidbodyPosition = body.position;
                s.velocity = body.linearVelocity; s.angularVelocity = body.angularVelocity;
            }
            Collider collider = robot.GetComponent<Collider>();
            s.hasCollider = collider != null;
            if (collider != null) s.colliderCentre = collider is BoxCollider box ? robot.TransformPoint(box.center) : collider.bounds.center;
            WheelCollider[] wheels = robot.GetComponentsInChildren<WheelCollider>();
            s.wheels = new Wheel[wheels.Length];
            for (int i = 0; i < wheels.Length; i++)
            {
                WheelCollider w = wheels[i];
                w.GetWorldPose(out Vector3 visual, out _);
                bool grounded = w.GetGroundHit(out WheelHit hit);
                Vector3 centrePosition = w.transform.TransformPoint(w.center);
                s.wheelMean += centrePosition;
                s.wheels[i] = new Wheel { path = Path(w.transform), centre = centrePosition, visualPosition = visual,
                    grounded = grounded, contact = hit.point, rpm = w.rpm, torque = w.motorTorque,
                    brake = w.brakeTorque, sidewaysSlip = hit.sidewaysSlip, forwardSlip = hit.forwardSlip };
            }
            if (wheels.Length > 0) s.wheelMean /= wheels.Length;
            return JsonUtility.ToJson(s);
        }
    }
}
