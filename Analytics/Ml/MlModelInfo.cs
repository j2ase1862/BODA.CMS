using System.Text.Json;
using System.Text.Json.Serialization;

namespace BODA.CMS.Analytics.Ml
{
    /// <summary>모델 사이드카(models/anomaly_iforest.json) — 학습 스크립트가 함께 export하는 메타.</summary>
    public sealed class MlModelInfo
    {
        [JsonPropertyName("window")] public int Window { get; set; } = 10;
        [JsonPropertyName("threshold")] public double Threshold { get; set; }
        [JsonPropertyName("featureNames")] public string[] FeatureNames { get; set; } = Array.Empty<string>();
        [JsonPropertyName("trainedAtUtc")] public string? TrainedAtUtc { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }

        public static MlModelInfo Load(string jsonPath) =>
            JsonSerializer.Deserialize<MlModelInfo>(File.ReadAllText(jsonPath))
                ?? throw new InvalidOperationException($"모델 사이드카 파싱 실패: {jsonPath}");
    }
}
