using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MetaMarine.VR
{
    public sealed class MissionPanel : MonoBehaviour
    {
        private MissionApiClient api;
        private VoiceTranscriptPanel voice;
        private Camera head;
        private string draft = "";
        private Font font;
        private GameObject hud;
        private Text hudText, speechText, commandText, actionText, resultText, modeText, footerText;
        private Image meterFill, progressFill, actionBackground;
        private bool wasRecording;
        private Sprite cardSprite;
        private Texture2D cardTexture;
        private Text stageText, progressCount;
        private Image statusAccent;
        private GameObject helpWindow, graphWindow;
        private readonly List<Button> vrButtons = new();
        private readonly List<GameObject> graphEquipment = new();
        private readonly List<VrSignalGraph> signalGraphs = new();
        private ShipRobot.Navigation.NavigationCoordinator graphNavigation;
        private string displayedGraphResult;
        private InputAction clickUi, pointerPosition, pointerRotation, pointerTracked;
        private LineRenderer pointerLine;
        private Material overlayMaterial, laserMaterial;
        private Button recordButton, startButton, stopButton;
        private Text recordLabel;
        private RectTransform gazeDot;
        private Text graphTitle;
        private const string HelpText = "음성으로 점검하기\n\n1. A를 누르고 ‘설비 A 점검해줘’처럼 말합니다.\n2. A를 다시 눌러 녹음을 마칩니다.\n3. 인식 문장을 확인하고 Y로 점검을 시작합니다.\n\nX  마이크 변경     B  시점 정렬\n오른쪽 스틱 클릭  취소 요청\n왼쪽 스틱 클릭  다음 진단 결과\n왼쪽 그립  패널 숨기기 / 열기\n\n오른쪽 컨트롤러로 버튼을 가리키고 검지 트리거로 선택합니다.\n새 임무를 실행하려면 Unity Play를 다시 시작하세요.";
        private readonly Color accent = new Color(0.20f, 0.90f, 0.77f);
        private InputAction submit, cancel, page;
        private InputAction togglePanel;
        private bool panelVisible = true;
        private int resultPage;
        private void OnEnable()
        {
            api = GetComponent<MissionApiClient>() ?? gameObject.AddComponent<MissionApiClient>();
            voice = GetComponent<VoiceTranscriptPanel>();
            head = GetComponent<PcVrView>()?.headCamera;
            if (voice != null) { voice.TranscriptReady += ReceiveTranscript; voice.ExternalMissionUI = true; }
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Arial" }, 28);
            submit = Action("Submit mission", "<XRController>{LeftHand}/secondaryButton", "<Keyboard>/f5");
            cancel = Action("Cancel mission", "<XRController>{RightHand}/thumbstickClicked", "<Keyboard>/f6");
            page = Action("Next result", "<XRController>{LeftHand}/thumbstickClicked", "<Keyboard>/f4");
            togglePanel = new InputAction("Toggle mission panel", InputActionType.Button, "<Keyboard>/f3");
            togglePanel.AddBinding("<XRController>{LeftHand}/gripPressed");
            togglePanel.Enable();
            if (head == null) return;
            clickUi = Action("Select VR UI", "<XRController>{RightHand}/triggerPressed", "<Keyboard>/enter");
            pointerPosition = new InputAction("UI pointer position", InputActionType.Value, "<XRController>{RightHand}/devicePosition");
            pointerRotation = new InputAction("UI pointer rotation", InputActionType.Value, "<XRController>{RightHand}/deviceRotation");
            pointerTracked = new InputAction("UI pointer tracked", InputActionType.Button, "<XRController>{RightHand}/isTracked");
            pointerPosition.Enable(); pointerRotation.Enable(); pointerTracked.Enable();
            graphNavigation = FindAnyObjectByType<ShipRobot.Navigation.NavigationCoordinator>();
            BuildVrHud();
            var shader = Shader.Find("MetaMarine/VR Overlay");
            if (shader != null)
            {
                overlayMaterial = new Material(shader);
                foreach (var graphic in hud.GetComponentsInChildren<Graphic>(true)) graphic.material = overlayMaterial;
            }
            var laser = new GameObject("VR Right Controller Laser");
            laser.transform.SetParent(transform, false);
            pointerLine = laser.AddComponent<LineRenderer>();
            pointerLine.useWorldSpace = true; pointerLine.positionCount = 2;
            pointerLine.startWidth = .003f; pointerLine.endWidth = .0015f;
            laserMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            laserMaterial.renderQueue = 5000;
            pointerLine.sharedMaterial = laserMaterial;
            pointerLine.startColor = pointerLine.endColor = accent;
            pointerLine.enabled = false;
        }

        private RectTransform Box(string name, Transform parent, float x, float y, float width, float height, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
            var image = go.GetComponent<Image>(); image.color = colour; image.raycastTarget = false;
            if (height > 20 && cardSprite != null) { image.sprite = cardSprite; image.type = Image.Type.Sliced; }
            return rect;
        }
        private Text TextBlock(string name, float x, float y, float width, float height, int size, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(hud.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
            var text = go.GetComponent<Text>(); text.font = font; text.fontSize = size;
            text.color = colour; text.supportRichText = false; text.raycastTarget = false;
            text.alignment = TextAnchor.UpperLeft; text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }
        private void CreateCardSprite()
        {
            const int size = 64;
            const float radius = 16f;
            cardTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            cardTexture.wrapMode = TextureWrapMode.Clamp;
            cardTexture.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x - 31.5f) - 16f, 0f);
                    float dy = Mathf.Max(Mathf.Abs(y - 31.5f) - 16f, 0f);
                    float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                    cardTexture.SetPixel(x, y, new Color(1, 1, 1, alpha));
                }
            cardTexture.Apply();
            cardSprite = Sprite.Create(cardTexture, new Rect(0, 0, size, size), Vector2.one * .5f, 100, 0,
                SpriteMeshType.FullRect, new Vector4(18, 18, 18, 18));
        }
        private void BuildVrHud()
        {
            CreateCardSprite();
            hud = new GameObject("Mission HUD", typeof(RectTransform), typeof(Canvas), typeof(Image));
            hud.transform.SetParent(head.transform, false);
            hud.transform.localPosition = new Vector3(0, -0.16f, 1.6f);
            hud.transform.localScale = Vector3.one * 0.001f;
            hud.GetComponent<RectTransform>().sizeDelta = new Vector2(1160, 820);
            hud.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            hud.GetComponent<Canvas>().worldCamera = head;
            hud.GetComponent<Canvas>().overrideSorting = true;
            hud.GetComponent<Canvas>().sortingOrder = 30000;
            var background = hud.GetComponent<Image>();
            background.sprite = cardSprite; background.type = Image.Type.Sliced;
            background.color = new Color(.025f,.043f,.07f,.96f); background.raycastTarget = false;
            var muted = new Color(.64f,.73f,.82f);
            var card = new Color(.065f,.095f,.14f);
            Box("Brand mark", hud.transform, 32, 28, 64, 60, accent);
            var brand = TextBlock("Monogram", 39, 39, 50, 42, 28, new Color(.02f,.09f,.10f));
            brand.text = "M"; brand.alignment = TextAnchor.MiddleCenter; brand.fontStyle = FontStyle.Bold;
            TextBlock("Title", 114, 26, 640, 46, 35, Color.white).text = "MetaMarine";
            TextBlock("Subtitle", 116, 72, 670, 32, 22, muted).text = "로봇과 연결된 나의 점검 공간";
            Box("Mode badge", hud.transform, 818, 31, 310, 61, new Color(.07f,.19f,.19f));
            modeText = TextBlock("Mode", 835, 43, 276, 42, 19, accent);
            TextBlock("Steps", 36, 120, 620, 34, 23, muted).text = "말하기  →  문장 확인  →  점검";
            AddVrButton("진단 그래프", 678, 106, 220, () => OpenWindow(graphWindow));
            AddVrButton("사용법", 916, 106, 212, () => OpenWindow(helpWindow));
            Box("Speech card", hud.transform, 28, 168, 1104, 226, card);
            TextBlock("Command label", 52, 187, 1020, 30, 21, muted).text = "음성 명령";
            commandText = TextBlock("Recognized command", 52, 230, 1056, 99, 37, Color.white);
            speechText = TextBlock("Speech status", 52, 337, 1028, 40, 22, accent);
            Box("Mic track", hud.transform, 52, 381, 1056, 4, new Color(.13f,.21f,.27f));
            meterFill = Box("Mic meter", hud.transform, 52, 381, 0, 4, accent).GetComponent<Image>();
            recordButton = AddVrButton("녹음 시작", 28, 409, 352, () => { if (voice != null) voice.ToggleRecording(); });
            startButton = AddVrButton("점검 시작", 404, 409, 352, Submit);
            stopButton = AddVrButton("임무 취소", 780, 409, 352, () => api.Cancel());
            recordLabel = recordButton.GetComponentInChildren<Text>();
            actionText = startButton.GetComponentInChildren<Text>();
            Box("Progress card", hud.transform, 28, 486, 362, 228, card);
            Box("Result card", hud.transform, 404, 486, 728, 228, card);
            statusAccent = Box("Status accent", hud.transform, 48, 506, 5, 29, accent).GetComponent<Image>();
            stageText = TextBlock("Stage", 66, 503, 302, 35, 25, Color.white);
            progressCount = TextBlock("Count", 50, 548, 316, 37, 27, accent);
            hudText = TextBlock("Mission status", 50, 603, 316, 96, 20, muted);
            Box("Progress track", hud.transform, 50, 592, 316, 5, new Color(.13f,.21f,.27f));
            progressFill = Box("Progress", hud.transform, 50, 592, 0, 5, accent).GetComponent<Image>();
            resultText = TextBlock("Result", 428, 503, 680, 197, 23, Color.white);
            Box("Footer divider", hud.transform, 32, 732, 1096, 1, new Color(.14f,.21f,.28f));
            footerText = TextBlock("Controls", 36, 747, 1090, 64, 21, muted);
            footerText.text = "오른쪽 컨트롤러로 가리키고 트리거로 선택";
            footerText.rectTransform.sizeDelta = new Vector2(620, 40);

            BuildWindows();
            gazeDot = Box("Gaze cursor", hud.transform, 0, 0, 9, 9, accent);
            gazeDot.gameObject.SetActive(false);
        }

        private Button AddVrButton(string title, float x, float y, float width, UnityEngine.Events.UnityAction action)
        {
            var rect = Box(title + " button", hud.transform, x, y, width, 52, new Color(.12f,.25f,.30f));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>(); button.onClick.AddListener(action);
            var label = TextBlock(title, x + 10, y + 8, width - 20, 38, 24, Color.white);
            label.text = title;
            label.alignment = TextAnchor.MiddleCenter;
            label.transform.SetParent(rect, true);
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10, 4); labelRect.offsetMax = new Vector2(-10, -4);
            button.transition = Selectable.Transition.None;
            vrButtons.Add(button); return button;
        }
        private void OpenWindow(GameObject window)
        {
            helpWindow.SetActive(window == helpWindow);
            graphWindow.SetActive(window == graphWindow);
        }
        private void BuildWindows()
        {
            var main = hud;
            helpWindow = Box("사용법 창", main.transform, 0, 0, 1160, 820, new Color(.025f,.043f,.07f,1)).gameObject;
            hud = helpWindow;
            TextBlock("Help title", 38, 28, 870, 55, 34, accent).text = "사용법";
            TextBlock("Help content", 42, 116, 1070, 664, 28, Color.white).text = HelpText;
            AddVrButton("닫기", 948, 28, 174, () => OpenWindow(null));
            graphWindow = Box("진단 그래프 창", main.transform, 0, 0, 1160, 820, new Color(.025f,.043f,.07f,1)).gameObject;
            hud = graphWindow;
            graphTitle = TextBlock("Graph title", 34, 24, 540, 48, 32, accent);
            AddVrButton("설비 A", 590, 26, 156, () => SelectGraph(0));
            AddVrButton("설비 B", 762, 26, 156, () => SelectGraph(1));
            AddVrButton("닫기", 948, 26, 174, () => OpenWindow(null));
            for (int i = 0; i < 2; i++)
            {
                hud = graphWindow;
                var group = Box("Equipment " + (i == 0 ? "A" : "B"), graphWindow.transform, 0, 96, 1160, 624, Color.clear).gameObject;
                graphEquipment.Add(group); hud = group;
                for (int j = 0; j < 2; j++)
                {
                    float top = j * 300;
                    var caption = TextBlock("Graph caption", 38, top + 8, 1080, 76, 20, Color.white);
                    var rect = Box("Signal plot", hud.transform, 38, top + 86, 1084, 194, new Color(.045f,.07f,.11f));
                    var plotObject = new GameObject("Waveform", typeof(RectTransform), typeof(CanvasRenderer), typeof(VrSignalGraph));
                    plotObject.transform.SetParent(rect, false);
                    var plotRect = plotObject.GetComponent<RectTransform>();
                    plotRect.anchorMin = Vector2.zero; plotRect.anchorMax = Vector2.one;
                    plotRect.offsetMin = new Vector2(8, 8); plotRect.offsetMax = new Vector2(-8, -8);
                    var graph = plotObject.GetComponent<VrSignalGraph>();
                    signalGraphs.Add(graph);
                    graph.equipmentId = i == 0 ? "A" : "B"; graph.vibration = j == 1; graph.caption = caption; graph.raycastTarget = false;
                }
            }
            hud = graphWindow;
            TextBlock("Graph provenance", 38, 733, 1080, 74, 21, new Color(.65f,.75f,.85f)).text =
                "저장 CSV 재생 · 약 10Hz 표시 표본 · 첫 점검부터 임무 종료까지 수집 · 종료 시 최근 30초 보존 · 원본 파형 아님\n전류와 진동은 독립 기록 · 세로축 자동 크기 · 물리 단위 미지정 · AI 판정은 메인 결과에서 확인";
            SelectGraph(0); hud = main;
            helpWindow.SetActive(false); graphWindow.SetActive(false);
        }
        private void SelectGraph(int index)
        {
            graphTitle.text = "설비 " + (index == 0 ? "A" : "B") + " / 전류 · 진동";
            for (int i = 0; i < graphEquipment.Count; i++) graphEquipment[i].SetActive(i == index);
        }
        private void UpdateVrButtons()
        {
            gazeDot.gameObject.SetActive(false);
            pointerLine.enabled = false;
            if (!panelVisible || !head.enabled) return;
            var controller = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            Vector3 position; Quaternion rotation;
            bool tracked = controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool deviceTracked) && deviceTracked &&
                controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out position) &&
                controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out rotation);
            position = Vector3.zero; rotation = Quaternion.identity;
            if (tracked)
            {
                controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out position);
                controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out rotation);
            }
            else if (pointerTracked.IsPressed())
            { position = pointerPosition.ReadValue<Vector3>(); rotation = pointerRotation.ReadValue<Quaternion>(); tracked = true; }
            if (!tracked) { footerText.text = "오른쪽 컨트롤러 추적 대기 · 컨트롤러를 켜고 움직여주세요"; return; }
            footerText.text = "레이저로 버튼을 가리키고 오른쪽 트리거를 누르세요";
            var origin = GetComponent<Unity.XR.CoreUtils.XROrigin>();
            Transform trackingSpace = origin != null && origin.CameraFloorOffsetObject != null
                ? origin.CameraFloorOffsetObject.transform : head.transform.parent;
            if (trackingSpace == null) return;
            var ray = new Ray(trackingSpace.TransformPoint(position), trackingSpace.rotation * rotation * Vector3.forward);
            pointerLine.enabled = true;
            pointerLine.SetPosition(0, ray.origin);
            pointerLine.SetPosition(1, ray.GetPoint(2f));
            var plane = new Plane(hud.transform.forward, hud.transform.position);
            if (!plane.Raycast(ray, out float distance)) return;
            Vector3 hit = ray.GetPoint(distance);
            if (!hud.GetComponent<RectTransform>().rect.Contains(hud.transform.InverseTransformPoint(hit))) return;
            pointerLine.SetPosition(1, hit);
            gazeDot.gameObject.SetActive(true);
            gazeDot.position = hit - head.transform.forward * .003f;
            gazeDot.SetAsLastSibling();
            Button hovered = null;
            foreach (var button in vrButtons)
            {
                bool modal = helpWindow.activeSelf || graphWindow.activeSelf;
                bool eligible = button.interactable && button.gameObject.activeInHierarchy && (!modal ||
                    button.transform.IsChildOf(helpWindow.activeSelf ? helpWindow.transform : graphWindow.transform));
                var rect = button.GetComponent<RectTransform>();
                bool inside = eligible && rect.rect.Contains(rect.InverseTransformPoint(hit));
                button.targetGraphic.color = !button.interactable ? new Color(.10f,.13f,.17f) : inside ? accent : new Color(.12f,.38f,.43f);
                if (inside) hovered = button;
            }
            if (hovered != null && clickUi.WasPressedThisFrame()) hovered.onClick.Invoke();
        }

        private static InputAction Action(string name, string xr, string keyboard)
        {
            var action = new InputAction(name, InputActionType.Button);
            action.AddBinding(xr); action.AddBinding(keyboard); action.Enable(); return action;
        }
        private void ReceiveTranscript(string text) { draft = text; panelVisible = true; if (helpWindow != null) OpenWindow(null); }
        private void Submit()
        {
            if (voice != null && (voice.IsRecording || voice.IsBusy)) return;
            if (api.Pending) { if (!api.Sending) api.RetryPending(); }
            else if (api.CanStart && !string.IsNullOrWhiteSpace(draft)) { resultPage = 0; api.Submit(draft); }
        }
        private void Update()
        {
            bool graphEnded = api.Snapshot?.Terminal == true || graphNavigation == null ||
                graphNavigation.State == ShipRobot.Navigation.NavigationCoordinator.MissionState.Completed ||
                graphNavigation.State == ShipRobot.Navigation.NavigationCoordinator.MissionState.Fault ||
                graphNavigation.State == ShipRobot.Navigation.NavigationCoordinator.MissionState.Idle;
            foreach (var graph in signalGraphs) graph.TickInspection(graphNavigation != null ? graphNavigation.CurrentInspectionPointName : null, graphEnded);
            if (api.Snapshot?.Terminal == true && api.Report != null && displayedGraphResult != api.Snapshot.mission_id && graphWindow != null)
            {
                displayedGraphResult = api.Snapshot.mission_id;
                panelVisible = true;
                SelectGraph(api.Snapshot.targets != null && api.Snapshot.targets.Length == 1 && api.Snapshot.targets[0] == "B" ? 1 : 0);
                OpenWindow(graphWindow);
            }
            bool recording = voice != null && voice.IsRecording;
            if (recording && !wasRecording) { draft = ""; panelVisible = true; if (helpWindow != null) OpenWindow(null); }
            wasRecording = recording;
            if (togglePanel.WasPressedThisFrame()) panelVisible = !panelVisible;
            if (panelVisible && (helpWindow == null || !helpWindow.activeSelf) && (graphWindow == null || !graphWindow.activeSelf) && submit.WasPressedThisFrame()) Submit();
            if (cancel.WasPressedThisFrame()) api.Cancel();
            if (page.WasPressedThisFrame()) resultPage++;
            if (hud == null) return;
            hud.SetActive(panelVisible && head.enabled);
            UpdateVrButtons();
            if (panelVisible && head.enabled)
            {
                string report = "";
                var points = api.Report?.points ?? api.Snapshot?.points;
                if (points != null && points.Length > 0)
                {
                    int pages = points.Length + 1;
                    int selected = resultPage % pages;
                    report = "\n결과 " + (selected + 1) + "/" + pages + "\n" +
                        (selected == 0 ? Clip(api.Report?.summary ?? api.Snapshot?.message, 240) : PointText(points[selected - 1], true));
                }
                modeText.text = api.Snapshot?.transport_mode == "mock" ? "모의 서버 / 테스트" : "시뮬레이션 / 저장 CSV";
                speechText.text = voice == null ? "음성 입력을 사용할 수 없습니다." :
                    voice.IsRecording ? $"● 녹음 중  {voice.RecordingSeconds:F0} / 15초   ·   A로 마치기" :
                    voice.IsBusy ? "음성 처리 중… 잠시 기다려주세요." : Clip(voice.Status, 65);
                commandText.text = string.IsNullOrWhiteSpace(draft)
                    ? (recording ? "말씀해주세요…" : "예: ‘설비 A 점검해줘’") : Clip(draft, 100);
                bool voiceWorking = voice != null && (voice.IsBusy || voice.IsRecording);
                bool canSend = !voiceWorking && ((api.CanStart && !string.IsNullOrWhiteSpace(draft)) || (api.Pending && !api.Sending));
                recordButton.interactable = voice != null && !voice.IsBusy;
                startButton.interactable = canSend;
                stopButton.interactable = api.CanCancel;
                recordLabel.text = recording ? "녹음 종료" : voice != null && !voice.IsReady ? "음성 서버 재연결" : "녹음 시작";
                actionText.text = api.Pending ? "접수 재확인" : voiceWorking ? "음성 처리 중" : "점검 시작";
                actionText.color = canSend ? Color.white : new Color(.48f,.55f,.62f);
                meterFill.rectTransform.sizeDelta = new Vector2(recording ? 1056 * voice.MeterLevel : 0, 4);
                stageText.text = api.Snapshot == null ? (api.Pending ? "접수 확인 중" : "점검 대기") : StateText().Split('|')[0].Trim();
                progressCount.text = api.Snapshot == null ? "— / — 지점" : $"{api.Snapshot.completed_points} / {api.Snapshot.total_points} 지점";
                bool warning = api.Snapshot?.requires_attention == true || api.Snapshot?.state == "FAILED";
                statusAccent.color = warning ? new Color(1f,.56f,.35f) : accent;
                stageText.color = warning ? statusAccent.color : Color.white;
                hudText.text = Clip(api.Notice, 65);
                var snapshot = api.Snapshot;
                progressFill.rectTransform.sizeDelta = new Vector2(snapshot == null || snapshot.total_points <= 0 ? 0 :
                    316 * Mathf.Clamp01((float)snapshot.completed_points / snapshot.total_points), 5);
                resultText.text = string.IsNullOrEmpty(report)
                    ? "점검 결과\n\n아직 완료된 진단이 없습니다.\n점검이 진행되면 이곳에서 확인할 수 있어요." : report.TrimStart();
            }
        }
        private static string Clip(string text, int max) => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text.Substring(0, max) + "…";
        private string Mode() => api.Snapshot?.transport_mode == "mock"
            ? "모의 서버 · 실제 이동/진단 아님" :
            (api.Snapshot?.agent_mode == "openai" ? "AI Agent · " : api.Snapshot?.agent_mode == "fixed_ab" ? "고정 명령 테스트 · " : "") +
            "Unity 시뮬레이션 · 저장 CSV 진단";
        private string StateText()
        {
            var state = api.Snapshot;
            if (state == null) return api.Pending ? "접수 확인 중 · 새 임무 전송 잠금" : "명령을 입력하고 전송하세요.";
            string name = state.state switch
            {
                "PENDING" => "접수됨", "PLANNING" => "계획 중", "EXECUTING" => "점검 진행 중",
                "DIAGNOSING" => "진단 중", "COMPLETED" => "완료", "FAILED" => "실패",
                "CANCELLING" => "취소 요청됨 · 정지 확인 중", "CANCELLED" => "취소 완료 · 정지 확인됨", _ => state.state
            };
            return name + " | 지점 " + state.completed_points + "/" + state.total_points +
                (state.requires_attention ? "\n추가 확인 필요 · 새 임무 잠금" : "");
        }
        private static string PointText(PointDiagnosis point, bool compact = false)
        {
            var text = new StringBuilder(point.equipment_id + " / " + point.point + "\n");
            if (point.status != "SUCCEEDED") return text + "미판정: " + point.message;
            foreach (var model in point.models)
            {
                string title = model.key switch { "axis" => "축 정렬", "bearing" => "베어링", "belt" => "벨트", "rotating" => "회전체", _ => model.key };
                text.AppendLine(title + ": " + (model.abnormal ? "이상" : "정상") + " · 이상 확률 " + model.abnormal_probability.ToString("P1"));
            }
            if (!compact && point.sample_count > 0) text.AppendLine("저장 CSV · " + point.sample_count + " 샘플 / " + point.sampling_frequency.ToString("F0") + " Hz");
            return text.ToString();
        }
        private void OnDisable()
        {
            if (voice != null) { voice.TranscriptReady -= ReceiveTranscript; voice.ExternalMissionUI = false; }
            submit?.Dispose(); cancel?.Dispose(); page?.Dispose();
            togglePanel?.Dispose(); clickUi?.Dispose();
            pointerPosition?.Dispose(); pointerRotation?.Dispose(); pointerTracked?.Dispose();
            if (pointerLine != null) Destroy(pointerLine.gameObject);
            if (overlayMaterial != null) Destroy(overlayMaterial);
            if (laserMaterial != null) Destroy(laserMaterial);
            vrButtons.Clear(); graphEquipment.Clear(); signalGraphs.Clear();
            if (hud != null) Destroy(hud);
            if (font != null) Destroy(font);
            if (cardSprite != null) Destroy(cardSprite);
            if (cardTexture != null) Destroy(cardTexture);
        }
    }
}
