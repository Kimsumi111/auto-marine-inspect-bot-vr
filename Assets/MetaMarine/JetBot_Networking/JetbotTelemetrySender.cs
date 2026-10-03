using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ShipRobot.LaneFollowing;
using UnityEngine;
using UnityEngine.XR;

namespace MetaMarine.VR
{
    // 제거 가능한 시험 입력의 연결점. 더미 CS 파일을 삭제해도 이 송신기는 컴파일된다.
    // REST/TCP 필드나 실제 HMD 수집 계약은 바꾸지 않는다.
    public interface IJetbotGazeOverride
    {
        bool IsOverrideEnabled { get; }
        string GazeOverrideStatus { get; }
        bool TryGetWorldDirection(out Vector3 direction);
    }

    // VR 앵커가 로봇 위치/방향을 갱신한 뒤 시선을 읽는다.
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public sealed class JetbotTelemetrySender : MonoBehaviour
    {
        [Header("순차 TCP — 기존 UDP 설정/시선 수집은 보존")]
        public bool useTcp = true;
        public int tcpBackendPort = 19101;

        [Header("로컬 Backend UDP — 기존 Mission 서버와 별도")]
        public int localPort = 19001;
        public int backendPort = 19002;

        [Range(1, 60)]
        public float sendHz = 20;

        [Range(50, 2000)]
        public int ttlMs = 200;

        [Header("jetbot / 로봇 기준 VR 시점")]
        public LaneFollowerController drive;
        public Rigidbody body;
        public PcVrView vrView;
        public Camera headCamera;

        [Header("연결 진단")]
        [SerializeField] string linkStatus = "중지";

        [Header("VR 시선 진단")]
        [SerializeField] string gazeStatus = "추적 대기";

        public string GazeStatus => gazeStatus;

        // UDP Connect는 목적지 지정일 뿐이다.
        // 서버가 보낸 ACK로 수신 여부를 판단한다.
        public bool BackendSeen =>
            useTcp ? tcpConnection != null && tcpConnection.Connected && LastAckSequence >= 1 :
            socket != null && Time.realtimeSinceStartupAsDouble - lastAck < 2;

        public string Status
        {
            get => linkStatus;
            private set => linkStatus = value;
        }

        public long SentCount { get; private set; }
        public long LastAckSequence { get; private set; } = -1;

        Socket socket;
        JetbotTcpConnection tcpConnection;
        string session;
        long sequence;

        double lastAck = double.NegativeInfinity;
        double nextSend;
        double nextHello;

        bool paused;

        MonoBehaviour gazeOverrideOwner;
        IJetbotGazeOverride gazeOverrideSource;

        // 같은 jetbot에 붙은 명시적인 시험 컴포넌트만 시선을 대체할 수 있다.
        public void RegisterGazeOverride(MonoBehaviour source)
        {
            if (source == null || source.gameObject != gameObject ||
                !(source is IJetbotGazeOverride gazeSource))
                throw new ArgumentException("같은 jetbot의 시선 입력 컴포넌트가 필요합니다.");
            gazeOverrideOwner = source;
            gazeOverrideSource = gazeSource;
        }

        public void UnregisterGazeOverride(MonoBehaviour source)
        {
            if (gazeOverrideOwner != source)
                return;
            gazeOverrideOwner = null;
            gazeOverrideSource = null;
        }

        readonly byte[] receiveBuffer = new byte[1201];
        readonly Packet packet = new Packet();

        [Serializable]
        public sealed class Movement
        {
            public bool valid;
            public bool drive_enabled;
            public bool safety_stopped;

            public float move;
            public float turn;
            public float forward_mps;
            public float yaw_rate_dps;

            public Vector3 position_world_m;
            public float body_yaw_deg;
        }

        [Serializable]
        public sealed class Gaze
        {
            public bool valid;
            public bool yaw_valid;

            public float yaw_deg;
            public float pitch_deg;

            public Vector3 direction_world;
            public Vector3 direction_robot_yaw;
        }

        [Serializable]
        public sealed class Packet
        {
            public int version = 1;

            public string type;
            public string session_id;
            public string source = "unity_simulation";
            public string robot_id = "jetbot";

            public long seq;
            public long sent_at_unix_ms;
            public int ttl_ms;

            // TCP 모드는 TTL을 사용하지 않고 각 샘플의 적용 시간을 보낸다.
            public string transport = "udp";
            public int duration_ms;

            public Movement movement = new Movement();
            public Gaze gaze = new Gaze();
        }

        [Serializable]
        sealed class Ack
        {
            public int version = 0;
            public string type = "";
            public string session_id = "";
            public long seq = 0;
        }

        void OnEnable()
        {
            // Unity 오브젝트 참조는 주 스레드에서만 읽는다.
            if (drive == null)
                drive = GetComponent<LaneFollowerController>();

            if (body == null)
                body = GetComponent<Rigidbody>();

            if (vrView == null)
                vrView = FindAnyObjectByType<PcVrView>();

            if (headCamera == null && vrView != null)
                headCamera = vrView.headCamera;

            // 재활성화 시 새 세션을 생성하여 이전 ACK와 구분한다.
            session = Guid.NewGuid().ToString();

            sequence = 0;
            SentCount = 0;
            LastAckSequence = -1;

            lastAck = double.NegativeInfinity;
            nextSend = nextHello = 0;
            paused = false;

            try
            {
                if (useTcp)
                {
                    if (tcpBackendPort < 1024 || tcpBackendPort > 65535)
                        throw new ArgumentException("TCP 포트를 확인하세요.");
                    tcpConnection = new JetbotTcpConnection("127.0.0.1", tcpBackendPort);
                    Status = "TCP 연결 대기 / 순차 송신 큐 사용";
                    Send("hello", false);
                    return;
                }
                if (localPort < 1024 || localPort > 65535 ||
                    backendPort < 1024 || backendPort > 65535 ||
                    localPort == backendPort)
                {
                    throw new ArgumentException("UDP 포트를 확인하세요.");
                }

                socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);

                socket.Bind(
                    new IPEndPoint(IPAddress.Loopback, localPort));

                socket.Connect(
                    new IPEndPoint(IPAddress.Loopback, backendPort));

                socket.Blocking = false;

                Status = "UDP 준비 / Backend ACK 대기";

                Debug.Log(
                    $"Jetbot telemetry: 127.0.0.1:{localPort}" +
                    $" → 127.0.0.1:{backendPort}",
                    this);

                Send("hello", false);
            }
            catch (Exception error)
            {
                Status = (useTcp ? "TCP" : "UDP") + " 초기화 실패: " + error.GetType().Name;
                Close();
                Debug.LogWarning(Status, this);
            }
        }

        void LateUpdate()
        {
            if (useTcp)
            {
                UpdateTcp();
                return;
            }
            if (socket == null)
                return;

            ReadAcks();

            double now = Time.realtimeSinceStartupAsDouble;

            // 서버가 나중에 실행되어도 수신 확인을 다시 시도한다.
            if (now >= nextHello)
            {
                nextHello = now + 1;

                if (!BackendSeen)
                    Send("hello", false);
            }

            if (now < nextSend)
                return;

            // 머리 각도가 이전 샘플과 같아도 현재 시선을 주기적으로 보낸다.
            // 밀린 프레임은 몰아서 보내지 않고 현재 최신 샘플 하나만 전송한다.
            nextSend = now + 1.0 / Mathf.Clamp(sendHz, 1, 60);

            Send("telemetry", !paused && Time.timeScale > 0);

            if (BackendSeen)
                Status = "Backend 수신 확인됨";
            else
                Status = "Backend ACK 없음 / UDP 송신 중";
        }

        void Capture(bool active)
        {
            Movement m = packet.movement;

            // 요청값이 아니라 기존 컨트롤러의 최종 제어값을 읽는다.
            m.valid =
                active &&
                drive != null &&
                drive.isActiveAndEnabled;

            m.drive_enabled = m.valid && drive.IsDriveEnabled;
            m.safety_stopped = drive != null && drive.IsSafetyStopped;

            m.move = m.valid ? drive.MoveCommand : 0;
            m.turn = m.valid ? drive.TurnCommand : 0;

            m.position_world_m = transform.position;
            m.body_yaw_deg = transform.eulerAngles.y;

            // 속도는 시뮬레이션 Rigidbody에서 관측한다.
            m.forward_mps = body != null
                ? Vector3.Dot(body.linearVelocity, transform.forward)
                : 0;

            m.yaw_rate_dps = body != null
                ? body.angularVelocity.y * Mathf.Rad2Deg
                : 0;

            CaptureGaze(active);
        }

        void CaptureGaze(bool active)
        {
            Gaze g = packet.gaze;

            // 매 송신마다 유효성을 새로 확인한다. 추적이 끊기면
            // 마지막 유효 방향을 현재 방향으로 재사용하지 않는다.
            g.valid = g.yaw_valid = false;
            g.yaw_deg = g.pitch_deg = 0;

            g.direction_world =
                g.direction_robot_yaw = Vector3.zero;

            if (!active)
            {
                gazeStatus = "시뮬레이션 일시정지 또는 비활성";
                return;
            }

            // 더미 입력은 실제 HMD 추적과 분리한다. 유효한 더미 방향도 같은 JSON/송신을 사용한다.
            // 초기 대기 중에는 무효 시선을 보내고, 비활성화/제거하면 아래 원래 HMD 경로를 사용한다.
            if (gazeOverrideOwner != null && gazeOverrideOwner.isActiveAndEnabled &&
                gazeOverrideSource.IsOverrideEnabled)
            {
                gazeStatus = gazeOverrideSource.GazeOverrideStatus;
                if (!gazeOverrideSource.TryGetWorldDirection(out Vector3 dummyDirection))
                    return;

                g.direction_world = dummyDirection.normalized;
                g.direction_robot_yaw = IntoRobotYaw(g.direction_world, transform.eulerAngles.y);
                CalculateAngles(g.direction_robot_yaw,
                    out g.yaw_deg, out g.pitch_deg, out g.yaw_valid);
                g.valid = true;
                return;
            }

            // 주행 중이 아니어도 VR 시점이 유효하면 시선을 보낸다.
            // 일반 카메라를 HMD 시선으로 오인하지 않도록 참조를 검사한다.
            if (vrView == null ||
                !vrView.isActiveAndEnabled ||
                headCamera == null ||
                headCamera != vrView.headCamera ||
                !headCamera.isActiveAndEnabled)
            {
                gazeStatus = "VR View / Head Camera 연결 또는 활성 상태 확인 필요";
                return;
            }

            RobotVrAnchor anchor =
                vrView.GetComponent<RobotVrAnchor>();

            // 이 VR 시점이 실제로 해당 jetbot을 기준으로 하는지 확인한다.
            if (anchor == null ||
                !anchor.isActiveAndEnabled ||
                anchor.robot != transform ||
                anchor.head != headCamera.transform)
            {
                gazeStatus = "VR 앵커의 jetbot / 머리 카메라 참조 확인 필요";
                return;
            }

            InputDevice head =
                InputDevices.GetDeviceAtXRNode(XRNode.Head);

            if (!head.isValid)
            {
                gazeStatus = "HMD 장치 없음";
                return;
            }

            if (!head.TryGetFeatureValue(
                    CommonUsages.isTracked, out bool tracked) ||
                !tracked)
            {
                gazeStatus = "HMD 추적 없음";
                return;
            }

            if (!head.TryGetFeatureValue(
                    CommonUsages.trackingState,
                    out InputTrackingState state) ||
                (state & InputTrackingState.Rotation) == 0)
            {
                gazeStatus = "HMD 회전 추적 없음";
                return;
            }

            // 변화량 기준이나 중복 각도 필터를 두지 않는다.
            // 머리를 고정한 경우에도 현재 방향을 읽고 새 seq로 전송한다.
            g.direction_world =
                headCamera.transform.forward.normalized;

            // 차체 기울기는 제외하고 로봇의 수평 전방을
            // 카메라 yaw 0도로 삼는다.
            g.direction_robot_yaw = IntoRobotYaw(
                g.direction_world,
                transform.eulerAngles.y);

            CalculateAngles(
                g.direction_robot_yaw,
                out g.yaw_deg,
                out g.pitch_deg,
                out g.yaw_valid);

            g.valid = true;
            gazeStatus = g.yaw_valid
                ? "현재 VR 시선 유효 / 동일 각도도 주기 송신"
                : "수직 시선 / pitch 유효, yaw 미확정";
        }

        // 월드 시선 벡터를 차체의 수평 yaw만큼 역회전한다.
        public static Vector3 IntoRobotYaw(
            Vector3 world,
            float bodyYawDegrees)
        {
            double radians = bodyYawDegrees * Math.PI / 180;

            float sin = (float)Math.Sin(radians);
            float cos = (float)Math.Cos(radians);

            return new Vector3(
                cos * world.x - sin * world.z,
                world.y,
                sin * world.x + cos * world.z);
        }

        // 오른쪽 yaw와 위쪽 pitch는 양수.
        // 수직 시선에서는 yaw를 확정할 수 없다.
        public static void CalculateAngles(
            Vector3 direction,
            out float yaw,
            out float pitch,
            out bool yawValid)
        {
            double horizontal = Math.Sqrt(
                direction.x * direction.x +
                direction.z * direction.z);

            yawValid = horizontal > 0.0001;

            yaw = yawValid
                ? (float)(
                    Math.Atan2(direction.x, direction.z) *
                    180 / Math.PI)
                : 0;

            pitch = (float)(
                Math.Atan2(direction.y, horizontal) *
                180 / Math.PI);
        }

        void Send(string type, bool active)
        {
            if (useTcp)
            {
                SendTcp(type, active);
                return;
            }
            if (socket == null)
                return;

            Capture(active);

            packet.type = type;
            packet.session_id = session;
            packet.seq = ++sequence;

            packet.sent_at_unix_ms =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            packet.ttl_ms = Mathf.Clamp(ttlMs, 50, 2000);

            byte[] bytes = Encoding.UTF8.GetBytes(
                JsonUtility.ToJson(packet));

            if (bytes.Length > 1200)
            {
                Status = "UDP 패킷 크기 초과";
                return;
            }

            try
            {
                socket.Send(bytes);
                SentCount++;
            }
            catch (SocketException error)
            {
                // 비차단 송신: 혼잡하면 샘플을 버리고
                // 다음 주기의 최신 값을 보낸다.
                Status = "UDP 송신 실패: " + error.SocketErrorCode;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void ReadAcks()
        {
            // 수신 횟수 상한으로 Unity 한 프레임을
            // 과도하게 점유하지 않는다.
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    if (!socket.Poll(0, SelectMode.SelectRead))
                        break;

                    int size = socket.Receive(receiveBuffer);

                    if (size > 1200)
                        continue;

                    Ack ack = JsonUtility.FromJson<Ack>(
                        Encoding.UTF8.GetString(
                            receiveBuffer, 0, size));

                    if (ack == null ||
                        ack.version != 1 ||
                        ack.type != "ack" ||
                        ack.session_id != session ||
                        ack.seq <= LastAckSequence ||
                        ack.seq > sequence)
                    {
                        continue;
                    }

                    LastAckSequence = ack.seq;
                    lastAck = Time.realtimeSinceStartupAsDouble;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ArgumentException)
                {
                }
            }
        }

        void OnApplicationPause(bool value)
        {
            paused = value;

            if (value)
                Send("telemetry", false);
        }

        void OnDisable()
        {
            Send("goodbye", false);
            Close();
        }

        void OnDestroy()
        {
            Close();
        }

        void OnApplicationQuit()
        {
            Send("goodbye", false);
            Close();
        }

        void Close()
        {
            tcpConnection?.Complete();
            tcpConnection = null;
            socket?.Dispose();
            socket = null;
            lastAck = double.NegativeInfinity;
            gazeStatus = "송신 중지";
        }

        void UpdateTcp()
        {
            if (tcpConnection == null)
                return;
            if (tcpConnection.Fault.Length != 0)
            {
                string faultStatus = "TCP 세션 중단: " + tcpConnection.Fault;
                if (Status != faultStatus)
                    Debug.LogError(faultStatus, this);
                Status = faultStatus;
                return;
            }
            // JsonUtility와 Unity 시간은 메인 스레드에서만 사용한다.
            while (tcpConnection.TryReadAck(out string json))
            {
                try
                {
                    Ack ack = JsonUtility.FromJson<Ack>(json);
                    if (ack != null && ack.version == 1 && ack.type == "ack" &&
                        ack.session_id == session && ack.seq > LastAckSequence && ack.seq <= sequence)
                    {
                        LastAckSequence = ack.seq;
                        lastAck = Time.realtimeSinceStartupAsDouble;
                    }
                }
                catch (ArgumentException)
                {
                    Status = "TCP ACK JSON 오류";
                }
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextSend)
            {
                // 실제로 수집하지 못한 과거 명령을 현재 값으로 만들어 채우지 않는다.
                // 프레임이 충분하면 목표 20Hz. 지연된 실제 TCP 송신은 큐에서 보존한다.
                double interval = 1.0 / Mathf.Clamp(sendHz, 1, 60);
                if (nextSend == 0)
                    nextSend = now;
                nextSend += interval;
                if (nextSend <= now)
                    nextSend = now + interval;
                Send("telemetry", !paused && Time.timeScale > 0);
            }
            Status = BackendSeen
                ? $"TCP 전달 확인 / ACK={LastAckSequence} 대기={tcpConnection.PendingCount}"
                : $"TCP 연결/ACK 대기 / 큐={tcpConnection.PendingCount}";
        }

        void SendTcp(string type, bool active)
        {
            if (tcpConnection == null || tcpConnection.Fault.Length != 0)
                return;
            Capture(active); // 최근 변경된 이동/VR 시선 수집을 그대로 사용한다.
            packet.type = type;
            packet.session_id = session;
            packet.seq = ++sequence;
            packet.sent_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            packet.ttl_ms = Mathf.Clamp(ttlMs, 50, 2000); // UDP 호환 필드. TCP에서 만료 검사 없음.
            packet.transport = "tcp";
            packet.duration_ms = type == "telemetry"
                ? Mathf.RoundToInt(1000f / Mathf.Clamp(sendHz, 1, 60)) : 0;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet));
            if (bytes.Length > 1200)
            {
                Status = "TCP 입력 계약 크기 초과 / 세션 중단";
                tcpConnection.Complete();
                Debug.LogError(Status, this);
                return;
            }
            if (tcpConnection.Enqueue(bytes))
                SentCount++; // TCP에서는 큐에 접수한 샘플 수. 전달 확인은 LastAckSequence.
        }
    }
}
