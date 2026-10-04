using System;
using System.Collections.Generic;
using ShipRobot.EquipmentMonitoring;
using UnityEngine;
using UnityEngine.UI;

namespace MetaMarine.VR
{
    // Display samples from the existing replay provider, not raw waveform or new measurements.
    public sealed class VrSignalGraph : MaskableGraphic
    {
        public string equipmentId = "A";
        public bool vibration;
        public Text caption;
        private CsvReplayDataSource source;
        private string file;
        private double lastPosition = -1;
        private readonly List<double[]> values = new();
        private readonly List<double> times = new();
        private float nextLookup;
        private static readonly Color[] Colours = { new(.2f,.9f,.77f), new(.3f,.65f,1f), new(1f,.72f,.3f) };
        private string capturedCaption;
        private bool started, finished;
        public void TickInspection(string point, bool missionEnded)
        {
            bool inspecting = !string.IsNullOrEmpty(point) && point.StartsWith("inspect_point_" + equipmentId, StringComparison.Ordinal);
            if (inspecting && !finished) started = true;
            if (started && missionEnded) finished = true;
            if (!started || finished)
            {
                if (caption != null) caption.text = !started
                    ? (vibration ? "진동" : "전류") + " · 설비 " + equipmentId + " 미점검"
                    : (capturedCaption ?? "수집 데이터 없음") + "\n임무 종료 · 최종 그래프";
                return;
            }
            Capture();
            capturedCaption = caption != null ? caption.text : "";
        }
        private void Capture()
        {
            if (source == null && Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 1;
                foreach (var candidate in FindObjectsByType<CsvReplayDataSource>())
                    if (candidate.Latest?.EquipmentId == equipmentId) { source = candidate; break; }
            }
            var snapshot = source != null ? source.Latest : null;
            var signal = vibration ? snapshot?.Vibration : snapshot?.Current;
            if (signal == null || snapshot.State == DataSourceState.Error || snapshot.State == DataSourceState.Stopped)
            {
                // Preserve collected results while the provider is unavailable.
                file = null; lastPosition = -1;
                if (caption != null) caption.text = (vibration ? "진동" : "전류") + " · 데이터 없음";
                return;
            }
            if (file != signal.SourcePath || signal.PositionSeconds < lastPosition)
            {
                if (values.Count > 0) { values.Add(new[] { double.NaN, double.NaN, double.NaN }); times.Add(Time.unscaledTimeAsDouble); }
                file = signal.SourcePath; lastPosition = -1;
            }
            if (signal.PositionSeconds != lastPosition)
            {
                var row = new double[signal.Values.Count];
                for (int i = 0; i < row.Length; i++) row[i] = signal.Values[i];
                values.Add(row); times.Add(Time.unscaledTimeAsDouble); lastPosition = signal.PositionSeconds;
                while (times.Count > 0 && (Time.unscaledTimeAsDouble - times[0] > 30 || times.Count > 600))
                { times.RemoveAt(0); values.RemoveAt(0); }
                SetVerticesDirty();
            }
            double peak = 0;
            foreach (var row in values) foreach (double v in row) if (!double.IsNaN(v) && !double.IsInfinity(v)) peak = Math.Max(peak, Math.Abs(v));
            string rms = "";
            for (int i = 0; i < signal.FileRms.Count; i++) rms += $" CH{i+1} {signal.FileRms[i]:F4}";
            if (caption != null) caption.text = $"{(vibration ? "진동 · CH1 민트" : "전류 · CH1 민트 / CH2 파랑 / CH3 노랑")}  [{snapshot.State}]\n" +
                $"세로 ±{peak:F4} · 최근 30초 → 현재 {signal.PositionSeconds:F1}s | 파일 RMS{rms}";
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); var r = rectTransform.rect;
            for (int i = 0; i <= 4; i++)
            {
                float y = r.yMin + r.height*i/4;
                Line(vh, new Vector2(r.xMin,y), new Vector2(r.xMax,y), new Color(.18f,.26f,.33f), 1);
            }
            if (values.Count < 2) return;
            double peak = 0.000001;
            foreach (var row in values) foreach (double v in row) if (!double.IsNaN(v) && !double.IsInfinity(v)) peak = Math.Max(peak,Math.Abs(v));
            double end = times[^1];
            double span = Math.Max(1, Math.Min(30, end - times[0]));
            for (int i = 1; i < values.Count; i++)
            {
                // Missing publishing intervals are gaps, not fabricated connecting waveforms.
                if (times[i] - times[i-1] > .5) continue;
                for (int ch = 0; ch < Math.Min(3,Math.Min(values[i-1].Length,values[i].Length)); ch++)
                {
                    double a = values[i-1][ch], b = values[i][ch];
                    if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) continue;
                    Line(vh, new Vector2(r.xMax+(float)((times[i-1]-end)/span)*r.width,r.center.y+(float)(a/peak)*r.height*.46f),
                        new Vector2(r.xMax+(float)((times[i]-end)/span)*r.width,r.center.y+(float)(b/peak)*r.height*.46f), Colours[ch], 1.8f);
                }
            }
        }
        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, Color tint, float width)
        {
            Vector2 n = new Vector2(-(b-a).y,(b-a).x).normalized * width/2;
            int start = vh.currentVertCount;
            vh.AddVert(a-n,tint,Vector2.zero); vh.AddVert(a+n,tint,Vector2.zero);
            vh.AddVert(b+n,tint,Vector2.zero); vh.AddVert(b-n,tint,Vector2.zero);
            vh.AddTriangle(start,start+1,start+2); vh.AddTriangle(start,start+2,start+3);
        }
    }
}
