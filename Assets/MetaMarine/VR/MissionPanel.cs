using System;
using System.Text;
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
        private string draft = "설비 A와 B를 점검해줘";
        private string address;
        private Font font;
        private GUIStyle label, button, input;
        private Vector2 scroll;
        private GameObject hud;
        private Text hudText;
        private InputAction submit, cancel, page;
        private int resultPage;
        private void OnEnable()
        {
            api = GetComponent<MissionApiClient>() ?? gameObject.AddComponent<MissionApiClient>();
            voice = GetComponent<VoiceTranscriptPanel>();
            head = GetComponent<PcVrView>()?.headCamera;
            address = api.baseUrl;
            if (voice != null) { voice.TranscriptReady += ReceiveTranscript; voice.ExternalMissionUI = true; }
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Arial" }, 28);
            submit = Action("Submit mission", "<XRController>{LeftHand}/secondaryButton", "<Keyboard>/f5");
            cancel = Action("Cancel mission", "<XRController>{RightHand}/thumbstickClicked", "<Keyboard>/f6");
            page = Action("Next result", "<XRController>{LeftHand}/thumbstickClicked", "<Keyboard>/f4");
            if (head == null) return;
            hud = new GameObject("Mission HUD", typeof(RectTransform), typeof(Canvas), typeof(Image));
            hud.transform.SetParent(head.transform, false);
            hud.transform.localPosition = new Vector3(0, -0.08f, 1.4f);
            hud.transform.localScale = Vector3.one * 0.001f;
            hud.GetComponent<RectTransform>().sizeDelta = new Vector2(1100, 680);
            hud.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            hud.GetComponent<Canvas>().worldCamera = head;
            hud.GetComponent<Image>().color = new Color(0.025f, 0.055f, 0.10f, 0.97f);
            hud.GetComponent<Image>().raycastTarget = false;
            var text = new GameObject("Mission text", typeof(RectTransform), typeof(Text));
            text.transform.SetParent(hud.transform, false);
            text.GetComponent<RectTransform>().sizeDelta = new Vector2(1040, 630);
            hudText = text.GetComponent<Text>(); hudText.font = font; hudText.fontSize = 27;
            hudText.color = Color.white; hudText.supportRichText = false; hudText.raycastTarget = false;
            hudText.alignment = TextAnchor.UpperLeft;
        }
        private static InputAction Action(string name, string xr, string keyboard)
        {
            var action = new InputAction(name, InputActionType.Button);
            action.AddBinding(xr); action.AddBinding(keyboard); action.Enable(); return action;
        }
        private void ReceiveTranscript(string text) { draft = text; }
        private void Submit()
        {
            if (api.Pending) api.RetryPending();
            else { resultPage = 0; api.Submit(draft); }
        }
        private void Update()
        {
            if (submit.WasPressedThisFrame()) Submit();
            if (cancel.WasPressedThisFrame()) api.Cancel();
            if (page.WasPressedThisFrame()) resultPage++;
            if (hud == null) return;
            hud.SetActive(head.enabled);
            if (head.enabled)
            {
                string report = "";
                if (api.Report != null)
                {
                    int pages = api.Report.points.Length + 1;
                    int selected = resultPage % pages;
                    report = "\n결과 " + (selected + 1) + "/" + pages + "\n" +
                        (selected == 0 ? Clip(api.Report.summary, 240) : PointText(api.Report.points[selected - 1]));
                }
                hudText.text = "설비 점검 | " + Mode() + "\n" + StateText() + "\n" + Clip(api.Notice, 110) +
                    "\n명령: " + Clip(draft, 120) + "\n" + (voice != null ? Clip(voice.StatusSummary, 150) : "") +
                    "\nY/F5 전송·재확인 | 오른쪽 스틱 클릭/F6 취소" +
                    "\nA/F8 녹음 | X/F7 마이크 | 왼쪽 스틱 클릭/F4 결과" + report;
            }
        }
        private static string Clip(string text, int max) => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text.Substring(0, max) + "…";
        private string Mode() => api.Snapshot?.transport_mode == "mock"
            ? "모의 서버 · 실제 이동/진단 아님" : "Unity 시뮬레이션 · 저장 CSV 진단";
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
            return name + " | 지점 " + state.completed_points + "/" + state.total_points + " | " + state.step +
                (state.requires_attention ? "\n추가 확인 필요 · 새 임무 잠금" : "");
        }
        private static string PointText(PointDiagnosis point)
        {
            var text = new StringBuilder(point.equipment_id + " / " + point.point + "\n");
            if (point.status != "SUCCEEDED") return text + "미판정: " + point.message;
            foreach (var model in point.models)
            {
                string title = model.key switch { "axis" => "축 정렬", "bearing" => "베어링", "belt" => "벨트", "rotating" => "회전체", _ => model.key };
                text.AppendLine(title + ": " + (model.abnormal ? "이상" : "정상") + " · 이상 확률 " + model.abnormal_probability.ToString("P1"));
            }
            return text.ToString();
        }
        private void OnGUI()
        {
            GUI.depth = -10000;
            label ??= new GUIStyle(GUI.skin.label) { font = font, fontSize = 18, wordWrap = true, richText = false };
            button ??= new GUIStyle(GUI.skin.button) { font = font, fontSize = 17 };
            input ??= new GUIStyle(GUI.skin.textArea) { font = font, fontSize = 19, wordWrap = true };
            float width = Mathf.Min(650, Screen.width - 24), height = Mathf.Min(740, Screen.height - 24);
            var rect = new Rect(12, 12, width, height);
            var old = GUI.color; GUI.color = new Color(0.025f, 0.055f, 0.10f, 0.98f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old;
            GUILayout.BeginArea(new Rect(24, 24, width - 24, height - 24));
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("설비 A + B 점검 | " + Mode(), label);
            GUILayout.Label(StateText(), label);
            GUILayout.Label(api.Notice, label);
            GUI.enabled = api.CanStart;
            draft = GUILayout.TextArea(draft, 500, input, GUILayout.Height(70));
            GUI.enabled = api.CanStart || (api.Pending && !api.Sending);
            if (GUILayout.Button(api.Pending ? "같은 요청 재확인/재전송 (F5)" : "점검 요청 전송 (F5)", button)) Submit();
            GUI.enabled = api.CanCancel;
            if (GUILayout.Button("임무 취소 요청 (F6)", button)) api.Cancel();
            GUI.enabled = true;
            if (voice != null)
            {
                GUILayout.Space(8); GUILayout.Label(voice.StatusSummary, label);
                if (GUILayout.Button("녹음 시작/종료 (A / F8)", button)) voice.ToggleRecording();
                if (GUILayout.Button("마이크 변경 (X / F7)", button)) voice.CycleMicrophone();
            }
            if (api.Report != null)
            {
                GUILayout.Space(12); GUILayout.Label(api.Report.summary, label);
                foreach (var point in api.Report.points) GUILayout.Label(PointText(point), label);
                GUILayout.Label("확률은 모델별 독립 결과입니다. 실제 센서 측정 결과가 아닙니다.", label);
            }
            GUILayout.Space(12); GUILayout.Label("Backend 주소 (모의 서버: http://127.0.0.1:8877)", label);
            if (api.Pending) GUILayout.Label("접수 확인 중에도 주소를 수정할 수 있습니다. 이전 요청은 해당 서버에 보존됩니다.", label);
            GUI.enabled = api.CanChangeEndpoint;
            address = GUILayout.TextField(address, input);
            if (GUILayout.Button("주소 적용", button))
            {
                api.ChangeEndpoint(address);
            }
            GUI.enabled = true;
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        private void OnDisable()
        {
            if (voice != null) { voice.TranscriptReady -= ReceiveTranscript; voice.ExternalMissionUI = false; }
            submit?.Dispose(); cancel?.Dispose(); page?.Dispose();
            if (hud != null) Destroy(hud);
            if (font != null) Destroy(font);
            label = button = input = null;
        }
    }
}
