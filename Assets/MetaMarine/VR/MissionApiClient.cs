using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaMarine.VR
{
    public sealed class MissionApiClient : MonoBehaviour
    {
        public string baseUrl = "http://127.0.0.1:8767";
        public MissionSnapshot Snapshot { get; private set; }
        public MissionReport Report { get; private set; }
        public string Notice { get; private set; } = "요청 대기 · Backend 연결 필요";
        public bool Sending { get; private set; }
        public bool Cancelling { get; private set; }
        public bool Pending => pending != null;
        // Unknown receipt blocks new commands, not correction of a mistyped server.
        public bool CanChangeEndpoint => !Sending && !Cancelling &&
            (Snapshot == null || (Snapshot.Terminal && !Snapshot.requires_attention && Report != null));
        public bool CanStart => !Sending && !Cancelling && pending == null &&
            (Snapshot == null || (Snapshot.Terminal && !Snapshot.requires_attention && Report != null));
        public bool CanCancel => Snapshot != null && (!Snapshot.Terminal || Snapshot.requires_attention) && !Cancelling;
        private MissionRequest pending;
        private readonly HashSet<UnityWebRequest> requests = new();
        private string storageKey;
        private string sessionUrl;
        private float nextPoll;
        private bool polling;
        [Serializable] private class Saved { public MissionRequest request; public string mission_id; }
        private Saved saved;

        private void OnEnable()
        {
            Snapshot = null; Report = null; pending = null; nextPoll = 0;
            baseUrl = PlayerPrefs.GetString("MetaMarine.Endpoint." + Application.dataPath, baseUrl);
            sessionUrl = baseUrl.TrimEnd('/');
            Notice = "요청 대기 · 서버: " + sessionUrl;
            storageKey = "MetaMarine.Mission." + Application.dataPath + "." + sessionUrl;
            try
            {
                saved = JsonConvert.DeserializeObject<Saved>(PlayerPrefs.GetString(storageKey, ""));
                if (saved?.request != null)
                {
                    pending = saved.request;
                    Notice = "이전 요청 상태 확인 중 · 자동 재실행하지 않습니다.";
                }
            }
            catch (Exception) { Notice = "저장된 요청을 읽지 못했습니다. Backend 상태를 먼저 확인하세요."; enabled = false; }
        }

        public void Submit(string text)
        {
            if (!CanStart) return;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 500) { Notice = "명령을 1~500자로 입력하세요."; return; }
            Snapshot = null; Report = null;
            pending = new MissionRequest { request_id = Guid.NewGuid().ToString(), text = text };
            saved = new Saved { request = pending };
            Save(); // Persist before sending, so Play restarts cannot silently create a duplicate.
            StartCoroutine(SendPending());
        }
        public void ChangeEndpoint(string address)
        {
            if (!CanChangeEndpoint) return;
            address = address?.Trim().TrimEnd('/');
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            { Notice = "서버 주소를 http://호스트:포트 형식으로 입력하세요."; return; }
            if (address == sessionUrl) { Notice = "이미 적용된 주소입니다."; return; }
            bool retained = pending != null;
            // OnDisable stops old polling; the previous server's saved request is untouched.
            enabled = false;
            baseUrl = address;
            PlayerPrefs.SetString("MetaMarine.Endpoint." + Application.dataPath, address);
            PlayerPrefs.Save();
            enabled = true;
            if (retained && !Pending)
                Notice = "주소 변경 완료 · 이전 서버의 미확인 요청은 보존했습니다. 서버 변경은 임무 취소가 아닙니다.";
        }
        public void RetryPending()
        {
            if (pending != null && !Sending) StartCoroutine(SendPending());
        }
        private void Save() { PlayerPrefs.SetString(storageKey, JsonConvert.SerializeObject(saved)); PlayerPrefs.Save(); }
        private void ClearSaved() { PlayerPrefs.DeleteKey(storageKey); PlayerPrefs.Save(); saved = null; }
        private IEnumerator SendPending()
        {
            Sending = true;
            var submitted = pending;
            yield return Request("POST", "/mission", JsonConvert.SerializeObject(submitted), (code, body) =>
            {
                if (code == 200 || code == 202) Accept(body, submitted.request_id);
                else if (code == 400 || code == 422 || code == 409)
                {
                    // These responses contractually mean no new mission was created.
                    Notice = Error(body, "요청 거절"); pending = null; ClearSaved();
                }
                else Notice = Error(body, "접수 여부 미확인 · 같은 요청 재확인/재전송을 사용하세요.");
            });
            Sending = false;
        }
        private void Accept(string body, string requestId)
        {
            try
            {
                var value = JsonConvert.DeserializeObject<MissionSnapshot>(body);
                value.Validate();
                if (value.request_id != requestId || (Snapshot != null && value.mission_id != Snapshot.mission_id))
                    throw new FormatException();
                if (Snapshot != null && value.revision < Snapshot.revision) return;
                if (Snapshot != null && value.state != Snapshot.state) Report = null;
                Snapshot = value; pending = null;
                if (saved != null) { saved.mission_id = value.mission_id; Save(); }
                Notice = value.message ?? "상태 수신";
            }
            catch (Exception) { Notice = "응답 계약 불일치 · 요청 상태를 유지합니다."; }
        }
        private void Update()
        {
            if (Time.unscaledTime < nextPoll || polling || Sending) return;
            if (pending != null || (Snapshot != null && (!Snapshot.Terminal || Report == null || Snapshot.requires_attention)))
                StartCoroutine(Poll());
        }
        private IEnumerator Poll()
        {
            polling = true;
            if (pending != null)
            {
                string id = pending.request_id;
                yield return Request("GET", "/mission/by-request/" + Uri.EscapeDataString(id), null, (code, body) =>
                {
                    if (code == 200) Accept(body, id);
                    else Notice = code == 404 ? "접수 기록 없음 · 같은 요청 재전송 가능" : "연결 끊김 · 접수 여부 확인 중";
                });
            }
            else if (Snapshot != null)
            {
                var current = Snapshot;
                yield return Request("GET", "/mission/" + Uri.EscapeDataString(current.mission_id), null, (code, body) =>
                {
                    if (code == 200) Accept(body, current.request_id);
                    else Notice = "상태 조회 실패 · 마지막 상태 표시 중 (정지 여부 미확인)";
                });
                if (Snapshot.Terminal && Report == null)
                    yield return Request("GET", "/mission/" + Uri.EscapeDataString(current.mission_id) + "/result", null, (code, body) =>
                    {
                        if (code != 200) { Notice = "최종 보고 조회 실패 · 재시도 중"; return; }
                        try
                        {
                            var report = JsonConvert.DeserializeObject<MissionReport>(body);
                            report.Validate(Snapshot); Report = report;
                            if (!Snapshot.requires_attention) ClearSaved();
                        }
                        catch (Exception) { Notice = "결과 계약 불일치 · 재조회 중"; }
                    });
            }
            nextPoll = Time.unscaledTime + 1f; polling = false;
        }
        public void Cancel()
        {
            if (CanCancel) StartCoroutine(CancelRequest());
        }
        private IEnumerator CancelRequest()
        {
            Cancelling = true;
            var current = Snapshot;
            yield return Request("POST", "/mission/" + Uri.EscapeDataString(current.mission_id) + "/cancel", "{}", (code, body) =>
            {
                if (code == 200 || code == 202) Accept(body, current.request_id);
                else Notice = "취소 응답 없음 · 정지 미확인 · 취소를 다시 요청할 수 있습니다.";
            });
            Cancelling = false;
        }
        private IEnumerator Request(string method, string path, string json, Action<long, string> complete)
        {
            if (!Uri.TryCreate(sessionUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            { complete(0, ""); yield break; }
            using (var web = new UnityWebRequest(sessionUrl + path, method))
            {
                web.downloadHandler = new DownloadHandlerBuffer(); web.timeout = 8;
                if (json != null) { web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)); web.SetRequestHeader("Content-Type", "application/json"); }
                requests.Add(web);
                yield return web.SendWebRequest();
                requests.Remove(web);
                complete(web.responseCode, web.downloadHandler.text);
            }
        }
        private static string Error(string json, string fallback)
        {
            try { return JsonConvert.DeserializeObject<MissionErrorResponse>(json)?.error?.message ?? fallback; }
            catch (Exception) { return fallback; }
        }
        private void OnDisable()
        {
            foreach (var web in requests) web.Abort();
            StopAllCoroutines(); requests.Clear(); Sending = polling = Cancelling = false;
            // UI teardown is not evidence of robot stop. Retain request identity for recovery.
        }
    }
}
