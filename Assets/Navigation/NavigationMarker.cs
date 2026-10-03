using UnityEngine;

namespace ShipRobot.Navigation
{
    [DisallowMultipleComponent]
    public sealed class NavigationMarker : MonoBehaviour
    {
        [SerializeField] private PlantNodeId nodeId;
        [SerializeField, Min(0.01f)] private float physicalSizeMetres = 0.30f;

        public enum MarkerRole { Entry, Centre }
        [SerializeField] private MarkerRole role;
        [SerializeField] private NavigationMarker centreMarker;
        [Tooltip("Virtual floor-plan NFC zone; not a physical NFC read range.")]
        [SerializeField, Min(0.01f)] private float proximityRadius = 0.45f;
        [Header("NFC debug display")]
        [SerializeField] private bool showNfcDisplay = true;
        [Tooltip("Display height only. Detection remains an XZ floor-plan zone.")]
        [SerializeField] private float displayFloorHeight = 0.05f;
        private GUIStyle nfcLabelStyle;
        private Color ZoneColour => role == MarkerRole.Entry
            ? new Color(0.05f, 0.9f, 1f) : new Color(1f, 0.25f, 0.85f);
        private string ZoneLabel => $"NFC {(role == MarkerRole.Entry ? "ENTRY" : "CENTRE")} | {nodeId}\nR {proximityRadius:F2} m  X {transform.position.x:F2}  Z {transform.position.z:F2}";
        public float ProximityRadius => proximityRadius;
        public MarkerRole Role => role;
        public NavigationMarker CentreMarker => centreMarker;

        // Simulation-only pair. Existing configured centres are never repositioned.
        public NavigationMarker EnsureCentre(Vector3 worldPosition)
        {
            if (centreMarker != null) return centreMarker;
            var go = new GameObject($"CentreQR_{nodeId}_SIM");
            go.transform.SetParent(transform, true);
            go.transform.position = worldPosition;
            centreMarker = go.AddComponent<NavigationMarker>();
            centreMarker.nodeId = nodeId;
            centreMarker.role = MarkerRole.Centre;
            centreMarker.physicalSizeMetres = physicalSizeMetres;
            Transform visual = transform.Find("AprilTagVisual");
            if (visual != null)
            {
                Transform copy = Instantiate(visual, go.transform);
                copy.name = "AprilTagVisual";
                copy.localPosition = visual.localPosition;
                copy.localRotation = visual.localRotation;
                copy.localScale = visual.localScale;
            }
            return centreMarker;
        }

        public PlantNodeId NodeId => nodeId;
        public float PhysicalSizeMetres => physicalSizeMetres;

        private Vector3 FloorCentre => new Vector3(transform.position.x, displayFloorHeight, transform.position.z);

        private void OnDrawGizmos()
        {
            if (!showNfcDisplay) return;
            Gizmos.color = ZoneColour;
            Vector3 centre = FloorCentre;
            for (int i = 0; i < 64; i++)
            {
                float a = i * Mathf.PI * 2f / 64f;
                float b = (i + 1) * Mathf.PI * 2f / 64f;
                Gizmos.DrawLine(centre + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * proximityRadius,
                    centre + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * proximityRadius);
            }
            Gizmos.DrawLine(centre + Vector3.left * .15f, centre + Vector3.right * .15f);
            Gizmos.DrawLine(centre + Vector3.back * .15f, centre + Vector3.forward * .15f);
            Gizmos.DrawLine(centre, transform.position);
            Gizmos.DrawWireCube(transform.position, Vector3.one * .15f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(centre + Vector3.up * .3f, ZoneLabel);
#endif
        }

        // Screen overlay only: never renders into the robot's perception RenderTexture.
        private void OnGUI()
        {
            if (!showNfcDisplay || Event.current.type != EventType.Repaint) return;
            Camera camera = Camera.main;
            if (camera == null) return;
            Vector3 centre = FloorCentre;
            Vector3 screen = camera.WorldToScreenPoint(centre);
            if (screen.z <= camera.nearClipPlane || !camera.pixelRect.Contains(new Vector2(screen.x, screen.y))) return;
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColour = GUI.color;
            int oldDepth = GUI.depth;
            GUI.depth = -90;
            GUI.color = ZoneColour;
            for (int i = 0; i < 64; i++)
            {
                float a = i * Mathf.PI * 2f / 64f;
                float b = (i + 1) * Mathf.PI * 2f / 64f;
                Vector3 p = camera.WorldToScreenPoint(centre + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * proximityRadius);
                Vector3 q = camera.WorldToScreenPoint(centre + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * proximityRadius);
                if (p.z > camera.nearClipPlane && q.z > camera.nearClipPlane)
                    DrawOverlayLine(new Vector2(p.x, Screen.height - p.y), new Vector2(q.x, Screen.height - q.y));
            }
            Vector2 c = new Vector2(screen.x, Screen.height - screen.y);
            DrawOverlayLine(c + Vector2.left * 8, c + Vector2.right * 8);
            DrawOverlayLine(c + Vector2.up * 8, c + Vector2.down * 8);
            if (nfcLabelStyle == null)
                nfcLabelStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            nfcLabelStyle.normal.textColor = ZoneColour;
            GUI.color = Color.white;
            float y = role == MarkerRole.Entry ? c.y - 56 : c.y + 12;
            GUI.Box(new Rect(Mathf.Clamp(c.x + 12, 0, Mathf.Max(0, Screen.width - 290)),
                Mathf.Clamp(y, 0, Mathf.Max(0, Screen.height - 44)), 290, 44), ZoneLabel, nfcLabelStyle);
            GUI.matrix = oldMatrix;
            GUI.color = oldColour;
            GUI.depth = oldDepth;
        }

        private static void DrawOverlayLine(Vector2 a, Vector2 b)
        {
            Matrix4x4 previous = GUI.matrix;
            Vector2 delta = b - a;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - 1.5f, delta.magnitude, 3f), Texture2D.whiteTexture);
            GUI.matrix = previous;
        }
    }
}
