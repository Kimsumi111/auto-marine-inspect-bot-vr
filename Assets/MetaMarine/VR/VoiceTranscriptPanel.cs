using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace MetaMarine.VR
{
    public sealed class VoiceTranscriptPanel : MonoBehaviour
    {
        public Camera headCamera;
        public string preferredMicrophone = "";
        public string Transcript { get; private set; } = "말한 내용이 여기에 표시됩니다.";
        public event Action<string> TranscriptReady;
        public bool ExternalMissionUI { get; set; }
        public bool IsRecording => recording;
        public bool IsBusy => busy;
        public bool IsReady => ready;
        public string Status => status;
        public string MicrophoneName => device ?? "없음";
        public float MeterLevel => Mathf.InverseLerp(-60f, -6f, 20f * Mathf.Log10(Mathf.Max(inputPeak, 0.000001f)));
        public float RecordingSeconds => recording ? Time.realtimeSinceStartup - started : 0f;
        public string StatusSummary => status + " · " + InputLevel + "\n마이크: " + (device ?? "없음");
        private const string Endpoint = "http://127.0.0.1:8766";
        private const int MaxSeconds = 15;
        private string status = "음성 서버 확인 중…";
        private string device;
        private AudioClip clip;
        private bool recording, busy, ready;
        private float started;
        private float inputPeak;
        private float[] meterSamples;
        private string InputLevel => inputPeak <= 0.000001f ? "입력 없음" : (20 * Mathf.Log10(inputPeak)).ToString("F0") + " dB";
        private InputAction toggle, nextMic;
        private Text statusText, transcriptText;
        private GameObject panel;
        private Font font;
        private UnityWebRequest request;
        private GUIStyle labelStyle;
        [Serializable] private class Reply { public string text; public string error; public bool ready; public string service; }

        private void OnEnable()
        {
            if (headCamera == null) headCamera = GetComponent<PcVrView>()?.headCamera;
            toggle = new InputAction("Toggle speech", InputActionType.Button);
            toggle.AddBinding("<XRController>{RightHand}/primaryButton");
            toggle.AddBinding("<Keyboard>/f8");
            nextMic = new InputAction("Next microphone", InputActionType.Button);
            nextMic.AddBinding("<XRController>{LeftHand}/primaryButton");
            nextMic.AddBinding("<Keyboard>/f7");
            toggle.Enable(); nextMic.Enable();
            ChooseMicrophone();
            CreatePanel();
            StartCoroutine(CheckHealth());
        }

        private void ChooseMicrophone()
        {
            var devices = Microphone.devices;
            device = devices.Length > 0 ? devices[0] : null;
            foreach (var item in devices)
                if ((!string.IsNullOrEmpty(preferredMicrophone) && item == preferredMicrophone) ||
                    (string.IsNullOrEmpty(preferredMicrophone) &&
                     (item.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      item.IndexOf("quest", StringComparison.OrdinalIgnoreCase) >= 0)))
                { device = item; break; }
        }

        public void CycleMicrophone()
        {
            if (recording || busy) return;
            var devices = Microphone.devices;
            device = devices.Length == 0 ? null : devices[(Array.IndexOf(devices, device) + 1) % devices.Length];
        }

        private IEnumerator CheckHealth()
        {
            busy = true;
            using (var web = UnityWebRequest.Get(Endpoint + "/health"))
            {
                request = web; web.timeout = 3;
                yield return web.SendWebRequest();
                ready = false;
                if (web.result == UnityWebRequest.Result.Success)
                {
                    var response = ParseReply(web.downloadHandler.text);
                    ready = response != null && response.ready && response.service == "metamarine-speech";
                }
                status = ready ? "대기 · A / F8로 녹음 시작" : "음성 서버 연결 안 됨 · 서버 시작 후 A / F8로 재확인";
                request = null;
            }
            busy = false;
        }

        private void Update()
        {
            if (toggle.WasPressedThisFrame()) ToggleRecording();
            if (nextMic.WasPressedThisFrame()) CycleMicrophone();
            if (recording && clip != null)
            {
                int position = Microphone.GetPosition(device);
                if (position >= 1024)
                {
                    if (meterSamples == null || meterSamples.Length != 1024 * clip.channels)
                        meterSamples = new float[1024 * clip.channels];
                    if (clip.GetData(meterSamples, position - 1024))
                    {
                        inputPeak = 0;
                        foreach (float value in meterSamples) inputPeak = Mathf.Max(inputPeak, Mathf.Abs(value));
                    }
                }
            }
            if (recording && Time.realtimeSinceStartup - started >= MaxSeconds) StopAndTranscribe();
            if (statusText != null)
                statusText.text = "음성 → 텍스트  |  " + status + " · " + InputLevel + "\n마이크: " + (device ?? "없음") +
                    "\nA/F8 시작·종료  ·  X/F7 마이크 변경  ·  B/F9 시점 재정렬";
            if (transcriptText != null) transcriptText.text = Transcript;
            if (panel != null) panel.SetActive(!ExternalMissionUI && headCamera != null && headCamera.enabled);
        }

        public void ToggleRecording()
        {
            if (busy) return;
            if (recording) { StopAndTranscribe(); return; }
            if (!ready) { StartCoroutine(CheckHealth()); return; }
            if (string.IsNullOrEmpty(device) || Array.IndexOf(Microphone.devices, device) < 0)
            { ChooseMicrophone(); status = "마이크를 연결하고 다시 시도해주세요."; return; }
            try
            {
                Microphone.GetDeviceCaps(device, out int minRate, out int maxRate);
                int captureRate = maxRate > 0 ? Mathf.Clamp(48000, Mathf.Max(1, minRate), maxRate) : 48000;
                inputPeak = 0;
                clip = Microphone.Start(device, false, MaxSeconds + 1, captureRate);
                if (clip == null) throw new InvalidOperationException();
                recording = true; started = Time.realtimeSinceStartup;
                status = "녹음 중 · 다시 A / F8 · 최대 15초";
            }
            catch (Exception) { status = "마이크 시작 실패 · Windows 마이크 권한과 입력 장치를 확인하세요."; }
        }

        private void StopAndTranscribe()
        {
            int frames = Microphone.GetPosition(device);
            recording = false;
            if (clip == null || frames < clip.frequency / 5)
            { Microphone.End(device); ReleaseClip(); status = "녹음이 너무 짧거나 입력이 없습니다."; return; }
            var samples = new float[frames * clip.channels];
            bool read = clip.GetData(samples, 0);
            Microphone.End(device);
            if (!read) { ReleaseClip(); status = "마이크 데이터를 읽지 못했습니다."; return; }
            inputPeak = 0;
            foreach (float value in samples) inputPeak = Mathf.Max(inputPeak, Mathf.Abs(value));
            if (inputPeak < 0.00001f)
            { ReleaseClip(); status = "마이크 입력 없음 · 헤드셋 음소거/Link 입력 확인 · X/F7로 다른 마이크 비교"; return; }
            byte[] wav = EncodeWav(samples, clip.channels, clip.frequency);
            ReleaseClip();
            StartCoroutine(Transcribe(wav));
        }

        public static byte[] EncodeWav(float[] samples, int channels, int sourceRate)
        {
            int frames = samples.Length / channels;
            int count = (int)((long)frames * 16000 / sourceRate);
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
            writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (int i = 0; i < count; i++)
            {
                float position = i * (float)sourceRate / 16000;
                int a = Mathf.Min((int)position, frames - 1), b = Mathf.Min(a + 1, frames - 1);
                float mono = 0;
                for (int c = 0; c < channels; c++) mono += Mathf.Lerp(samples[a * channels + c], samples[b * channels + c], position - a);
                writer.Write((short)(Mathf.Clamp(mono / channels, -1, 1) * 32767));
            }
            return memory.ToArray();
        }

        private IEnumerator Transcribe(byte[] wav)
        {
            busy = true; status = "한국어 인식 중…";
            using (var web = new UnityWebRequest(Endpoint + "/transcribe", "POST"))
            {
                request = web;
                web.uploadHandler = new UploadHandlerRaw(wav);
                web.downloadHandler = new DownloadHandlerBuffer();
                web.SetRequestHeader("Content-Type", "audio/wav"); web.timeout = 90;
                yield return web.SendWebRequest();
                if (web.result != UnityWebRequest.Result.Success)
                { status = "인식 실패 · 서버와 마이크를 확인하고 재시도하세요."; ready = false; }
                else
                {
                    var reply = ParseReply(web.downloadHandler.text);
                    if (string.IsNullOrWhiteSpace(reply?.text)) status = inputPeak < 0.002f
                        ? "입력 소리가 너무 작습니다 · 마이크 입력 볼륨을 확인하세요."
                        : "소리는 들어왔지만 말을 인식하지 못했습니다 · 문장을 또렷하게 다시 말해주세요.";
                    else { Transcript = reply.text; status = "인식 완료 · A / F8로 다시 녹음"; TranscriptReady?.Invoke(Transcript); }
                }
                request = null;
            }
            busy = false;
        }

        private static Reply ParseReply(string json)
        {
            try { return JsonUtility.FromJson<Reply>(json); }
            catch (ArgumentException) { return null; }
        }

        private void CreatePanel()
        {
            if (headCamera == null) return;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Arial" }, 32);
            panel = new GameObject("Voice Transcript HUD", typeof(RectTransform), typeof(Canvas), typeof(Image));
            panel.transform.SetParent(headCamera.transform, false);
            panel.transform.localPosition = new Vector3(0, -0.32f, 1.3f);
            panel.transform.localScale = Vector3.one * 0.001f;
            panel.GetComponent<RectTransform>().sizeDelta = new Vector2(980, 300);
            panel.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            panel.GetComponent<Canvas>().worldCamera = headCamera;
            panel.GetComponent<Image>().color = new Color(0.025f, 0.06f, 0.10f, 0.94f);
            panel.GetComponent<Image>().raycastTarget = false;
            statusText = AddText("Status", new Vector2(0, 78), new Vector2(920, 120), 25, new Color(0.3f, 0.95f, 0.85f));
            transcriptText = AddText("Transcript", new Vector2(0, -57), new Vector2(920, 130), 36, Color.white);
        }
        private Text AddText(string name, Vector2 position, Vector2 size, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(panel.transform, false);
            var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
            var text = go.GetComponent<Text>(); text.font = font; text.fontSize = fontSize; text.color = color;
            text.alignment = TextAnchor.MiddleLeft; text.supportRichText = false; text.raycastTarget = false;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 22; text.resizeTextMaxSize = fontSize;
            return text;
        }


        private void ReleaseClip() { if (clip != null) Destroy(clip); clip = null; }
        private void OnDisable()
        {
            if (recording) Microphone.End(device);
            recording = false; busy = false; ready = false;
            request?.Abort(); StopAllCoroutines(); request = null;
            ReleaseClip(); toggle?.Dispose(); nextMic?.Dispose();
            if (panel != null) Destroy(panel);
            if (font != null) Destroy(font);
            labelStyle = null;
        }
    }
}
