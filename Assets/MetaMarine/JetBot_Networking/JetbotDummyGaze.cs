using System;
using UnityEngine;

namespace MetaMarine.VR
{
    // 임시 시험용: 이 컴포넌트를 제거하면 HMD 시선 수집으로 돌아간다.
    // 실제 VR 추적값이 아니며, 활성화하면 실제 짐벌에 시험 명령이 전달될 수 있다.
    [DefaultExecutionOrder(190)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(JetbotTelemetrySender))]
    public sealed class JetbotDummyGaze : MonoBehaviour, IJetbotGazeOverride
    {
        [Header("더미 시선 — 실제 VR 입력 아님")]
        public bool testEnabled = true;
        public bool waitForBackendAck = true;
        [Min(0)] public float initialDelaySeconds = 1;
        [Min(0.1f)] public float holdSeconds = 1;
        [Range(0, 30)] public float yawDegrees = 30;
        [Range(0, 30)] public float pitchDegrees = 30;
        public bool repeatTest;

        [Header("독립적인 모의 1인칭 미리보기")]
        public bool showPreview = true;
        public Vector3 eyeOffset = new Vector3(0, 0.65f, 0.25f);
        [SerializeField] string testStatus = "더미 시험 대기";
        [SerializeField] float currentYaw;
        [SerializeField] float currentPitch;

        JetbotTelemetrySender sender;
        Camera previewCamera;
        RenderTexture previewTexture;
        double elapsed;
        double lastTick;
        bool directionReady;
        bool applicationPaused;
        int loggedStage = -2;

        public bool IsOverrideEnabled => testEnabled;
        public string GazeOverrideStatus => testStatus;

        void OnEnable()
        {
            sender = GetComponent<JetbotTelemetrySender>();
            sender.RegisterGazeOverride(this);
            CreatePreview();
            RestartTest();
        }

        [ContextMenu("더미 시선 시험 다시 시작")]
        public void RestartTest()
        {
            elapsed = 0;
            lastTick = Time.realtimeSinceStartupAsDouble;
            currentYaw = currentPitch = 0;
            directionReady = false;
            loggedStage = -2;
            testStatus = "더미 / 연결 및 초기 대기";
        }

        void LateUpdate()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double delta = Math.Max(0, now - lastTick);
            lastTick = now;

            if (!testEnabled || applicationPaused || Time.timeScale <= 0)
            {
                directionReady = false;
                testStatus = "더미 / 비활성 또는 일시정지";
                if (previewCamera != null) previewCamera.enabled = false;
                return;
            }

            if (waitForBackendAck && (sender == null || !sender.BackendSeen))
            {
                // 미연결 상태에서 시험 순서가 먼저 끝나거나 오래된 방향을 쌓지 않는다.
                elapsed = 0;
                directionReady = false;
                currentYaw = currentPitch = 0;
                testStatus = "더미 / Backend ACK 대기 (Jetson 수신기를 먼저 실행)";
                UpdatePreview();
                return;
            }

            elapsed += delta;
            directionReady = TryGetTestAngles(elapsed, initialDelaySeconds, holdSeconds,
                yawDegrees, pitchDegrees, repeatTest, out Vector2 angles, out int stage);
            currentYaw = angles.x;
            currentPitch = angles.y;
            string label = stage < 0 ? "초기 대기" :
                stage == 0 ? "좌" : stage == 1 ? "우" :
                stage == 2 ? "상" : stage == 3 ? "하" : "정면";
            testStatus = $"더미 / {label} / yaw={currentYaw:+0.0;-0.0;0.0} " +
                $"pitch={currentPitch:+0.0;-0.0;0.0}";
            UpdatePreview();

            // 20Hz마다 Console을 누적하지 않고 방향이 바뀔 때만 기록한다.
            if (loggedStage != stage)
            {
                loggedStage = stage;
                Debug.Log("[더미 시선] " + testStatus + " (실제 HMD 입력 아님)", this);
            }
        }

        // 단계 계산은 통신과 분리한다. 30도 상한은 현재 양쪽 축 허용 범위 안이다.
        // 기본은 1회 시험 후 정면 유지. 반복하면 정면 1구간을 포함해 다시 좌부터 시작한다.
        public static bool TryGetTestAngles(double time, float delay, float hold,
            float yaw, float pitch, bool repeat, out Vector2 angles, out int stage)
        {
            delay = Limit(delay, 1, 0, 60);
            hold = Limit(hold, 1, 0.1f, 60);
            yaw = Limit(yaw, 30, 0, 30);
            pitch = Limit(pitch, 30, 0, 30);
            angles = Vector2.zero;
            stage = -1;
            if (double.IsNaN(time) || double.IsInfinity(time) || time < delay)
                return false;

            double slot = (time - delay) / hold;
            if (repeat) slot %= 5;
            stage = slot >= 4 ? 4 : (int)slot;
            angles = stage == 0 ? new Vector2(-yaw, 0) :
                stage == 1 ? new Vector2(yaw, 0) :
                stage == 2 ? new Vector2(0, pitch) :
                stage == 3 ? new Vector2(0, -pitch) : Vector2.zero;
            return true;
        }

        static float Limit(float value, float fallback, float low, float high)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = fallback;
            return Math.Max(low, Math.Min(high, value));
        }

        // 로봇의 수평 전방 기준 각도를 월드 방향의 단위 벡터로 변환한다.
        // 오른쪽 yaw·위쪽 pitch는 양수. 기존 시선 함수로 다시 각도를 추출한다.
        public static Vector3 WorldDirection(float robotYaw, float yaw, float pitch)
        {
            double horizontal = (robotYaw + yaw) * Math.PI / 180;
            double vertical = pitch * Math.PI / 180;
            double cosPitch = Math.Cos(vertical);
            return new Vector3((float)(Math.Sin(horizontal) * cosPitch),
                (float)Math.Sin(vertical), (float)(Math.Cos(horizontal) * cosPitch));
        }

        void CreatePreview()
        {
            var cameraObject = new GameObject("Jetbot Dummy Gaze Camera (runtime)");
            cameraObject.transform.SetParent(transform, false);
            previewCamera = cameraObject.AddComponent<Camera>();
            previewCamera.enabled = false;
            previewCamera.stereoTargetEye = StereoTargetEyeMask.None;
            previewCamera.fieldOfView = 75;
            previewCamera.nearClipPlane = 0.03f;
            previewCamera.farClipPlane = 100;

            // 실행 중에만 생성한다. 씬이나 RenderTexture 에셋은 수정하지 않는다.
            previewTexture = new RenderTexture(320, 180, 16) { name = "Dummy Gaze Preview (runtime)" };
            previewTexture.Create();
            previewCamera.targetTexture = previewTexture;
        }

        void UpdatePreview()
        {
            if (previewCamera == null) return;
            Quaternion bodyYaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            Vector3 direction = WorldDirection(transform.eulerAngles.y, currentYaw, currentPitch);
            previewCamera.transform.SetPositionAndRotation(
                transform.position + bodyYaw * eyeOffset, Quaternion.LookRotation(direction, Vector3.up));
            previewCamera.enabled = showPreview;
        }

        public bool TryGetWorldDirection(out Vector3 direction)
        {
            // 숫자만 넣지 않고, 실제로 같은 각도를 향한 시험 카메라에서 방향을 읽는다.
            direction = directionReady && previewCamera != null
                ? previewCamera.transform.forward : Vector3.zero;
            return directionReady && previewCamera != null;
        }

        void OnGUI()
        {
            if (!testEnabled || !showPreview || previewTexture == null) return;
            float y = Math.Max(10, Screen.height - 250);
            GUI.Box(new Rect(10, y, 334, 238), "더미 시선 시험 / 실제 HMD 입력 아님");
            GUI.DrawTexture(new Rect(17, y + 25, 320, 180), previewTexture, ScaleMode.ScaleToFit, false);
            GUI.Label(new Rect(17, y + 208, 320, 25), testStatus);
        }

        void OnApplicationPause(bool value) => applicationPaused = value;

        void OnDisable()
        {
            if (sender != null) sender.UnregisterGazeOverride(this);
            directionReady = false;
            if (previewCamera != null)
            {
                previewCamera.enabled = false;
                previewCamera.targetTexture = null;
                Destroy(previewCamera.gameObject);
            }
            if (previewTexture != null)
            {
                previewTexture.Release();
                Destroy(previewTexture);
            }
            previewCamera = null;
            previewTexture = null;
        }
    }
}
