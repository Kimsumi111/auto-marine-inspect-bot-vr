using System;
using System.Linq;
using Newtonsoft.Json;

namespace MetaMarine.VR
{
    [Serializable] public sealed class MissionRequest
    {
        public int api_version = 1;
        public string request_id;
        public string text;
        public string execution_mode = "simulation";
    }
    [Serializable] public sealed class MissionError { public string code; public string message; }
    [Serializable] public sealed class MissionErrorResponse { public MissionError error; }
    [Serializable] public sealed class MissionSnapshot
    {
        [JsonProperty(Required = Required.Always)] public int api_version;
        [JsonProperty(Required = Required.Always)] public string mission_id;
        [JsonProperty(Required = Required.Always)] public string request_id;
        [JsonProperty(Required = Required.Always)] public long revision;
        [JsonProperty(Required = Required.Always)] public string state;
        [JsonProperty(Required = Required.Always)] public string transport_mode;
        [JsonProperty(Required = Required.Always)] public string execution_mode;
        [JsonProperty(Required = Required.Always)] public string data_mode;
        [JsonProperty(Required = Required.Always)] public int completed_points;
        [JsonProperty(Required = Required.Always)] public int total_points;
        [JsonProperty(Required = Required.Always)] public bool stop_confirmed;
        [JsonProperty(Required = Required.Always)] public bool requires_attention;
        public string step;
        public string message;
        public string unity_session_id;
        public string updated_at;
        public bool Terminal => state == "COMPLETED" || state == "FAILED" || state == "CANCELLED";
        public void Validate()
        {
            string[] states = { "PENDING", "PLANNING", "EXECUTING", "DIAGNOSING", "COMPLETED", "FAILED", "CANCELLING", "CANCELLED" };
            if (api_version != 1 || !Guid.TryParse(mission_id, out _) || !Guid.TryParse(request_id, out _) ||
                revision < 0 || !states.Contains(state) || (transport_mode != "backend" && transport_mode != "mock") ||
                execution_mode != "simulation" || data_mode != "offline_csv_replay" ||
                total_points != 4 || completed_points < 0 || completed_points > total_points ||
                (state == "CANCELLED" && !stop_confirmed)) throw new FormatException("Invalid mission snapshot");
        }
    }
    [Serializable] public sealed class ModelDiagnosis
    {
        [JsonProperty(Required = Required.Always)] public string key;
        [JsonProperty(Required = Required.Always)] public double abnormal_probability;
        [JsonProperty(Required = Required.Always)] public double threshold;
        [JsonProperty(Required = Required.Always)] public bool abnormal;
        public string model_sha256;
    }
    [Serializable] public sealed class PointDiagnosis
    {
        public string inspection_id;
        public string point;
        public string equipment_id;
        public string source_equipment_id;
        public string status; // SUCCEEDED / FAILED / NOT_EVALUATED
        public string message;
        public ModelDiagnosis[] models;
    }
    [Serializable] public sealed class MissionReport
    {
        [JsonProperty(Required = Required.Always)] public int api_version;
        public string mission_id;
        public string state;
        public string transport_mode;
        public string execution_mode;
        public string data_mode;
        public string summary;
        [JsonProperty(Required = Required.Always)] public PointDiagnosis[] points;
        public void Validate(MissionSnapshot snapshot)
        {
            if (api_version != 1 || mission_id != snapshot.mission_id || state != snapshot.state ||
                transport_mode != snapshot.transport_mode || execution_mode != "simulation" || data_mode != "offline_csv_replay" ||
                points == null || points.Length > 4 || points.Select(p => p.point).Distinct().Count() != points.Length)
                throw new FormatException("Invalid report");
            foreach (var point in points)
            {
                string[] names = { "inspect_point_A1", "inspect_point_A2", "inspect_point_B2", "inspect_point_B1" };
                if (!names.Contains(point.point) || point.equipment_id != (point.point.Contains("_A") ? "A" : "B") ||
                    !new[] { "SUCCEEDED", "FAILED", "NOT_EVALUATED" }.Contains(point.status)) throw new FormatException("Invalid point");
                if (point.status != "SUCCEEDED") continue;
                if (point.models == null || point.models.Length != 4 || point.models.Select(m => m.key).Distinct().Count() != 4)
                    throw new FormatException("Four independent diagnoses required");
                foreach (var model in point.models)
                    if (!new[] { "axis", "bearing", "belt", "rotating" }.Contains(model.key) ||
                        double.IsNaN(model.abnormal_probability) || model.abnormal_probability < 0 || model.abnormal_probability > 1 ||
                        model.threshold != 0.5 || model.abnormal != (model.abnormal_probability >= model.threshold))
                        throw new FormatException("Invalid diagnosis");
            }
            if (state == "COMPLETED" && (points.Length != 4 || points.Any(p => p.status != "SUCCEEDED")))
                throw new FormatException("Incomplete completed report");
        }
    }
}
