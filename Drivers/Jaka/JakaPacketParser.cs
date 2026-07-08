using System.Text.Json;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Drivers.Jaka
{
    /// <summary>
    /// JAKA 모니터 스트림 JSON 패킷 → 공용 프레임.
    ///
    /// 필드 매핑은 방어적이다 — 펌웨어/기종별로 존재 필드가 달라서, 관절 위치만 필수로 요구하고
    /// 나머지(전류/온도/토크/속도 후보)는 있으면 <b>스케일·단위 미확정 원시값(VendorRaw)</b>으로
    /// 보존한다(§2 규약: 추정 스케일로 정규화 필드를 오염시키지 않는다). 실기 검증(§6 4단계)에서
    /// 단위·스케일 확정 시 정규화 매핑과 capability를 함께 올린다.
    ///
    /// ⚠️ 위치 단위: 모니터 스트림의 joint_actual_position 은 도(°) 단위로 문서화돼 있으나
    ///    (SDK 함수는 라디안) 실기 1회 대조가 §6 체크리스트에 포함돼 있다.
    /// </summary>
    internal static class JakaPacketParser
    {
        public const string PositionKey = "joint_actual_position";

        // 존재가 펌웨어 의존적인 신호 후보 → VendorRaw 키 (짧은 이름 = UI 라벨 열 규약)
        private static readonly (string JsonKey, string RawKey)[] RawCandidates =
        {
            ("joint_actual_velocity", "vel_raw"),
            ("instCurrent", "cur_raw"),
            ("joint_current", "cur_raw"),
            ("joint_temp", "temp_raw"),
            ("temperature", "temp_raw"),
            ("torqsensor", "tor_raw"),
            ("joint_torque", "tor_raw"),
        };

        /// <summary>패킷 1건 파싱. 관절 위치가 없거나 형식 불량이면 null(스킵).</summary>
        public static RobotTelemetryFrame? Parse(string json, DateTime utcNow)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                // 일부 펌웨어는 {"len":N,"data":{...}} 래핑 — data 안쪽을 본다.
                if (root.TryGetProperty("data", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object)
                    root = inner;

                double[]? pos = ReadNumberArray(root, PositionKey);
                if (pos is null || pos.Length == 0) return null;

                Dictionary<string, double[]>? raw = null;
                foreach ((string jsonKey, string rawKey) in RawCandidates)
                {
                    if (raw is not null && raw.ContainsKey(rawKey)) continue; // 첫 매칭 우선
                    double[]? values = ReadNumberArray(root, jsonKey);
                    if (values is null) continue;
                    raw ??= new Dictionary<string, double[]>();
                    raw[rawKey] = values;
                }

                return new RobotTelemetryFrame
                {
                    ReceivedAtUtc = utcNow,
                    VendorId = JakaJsonSource.StaticCapabilities.VendorId,
                    ChannelId = JakaJsonSource.StaticCapabilities.ChannelId,
                    JointPositionDeg = Array.ConvertAll(pos, v => (float)v),
                    VendorRaw = raw,
                };
            }
            catch (JsonException)
            {
                return null; // 손상 패킷은 조용히 스킵 — 스트림은 계속
            }
        }

        private static double[]? ReadNumberArray(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Array) return null;
            var result = new List<double>(6);
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) return null;
                result.Add(item.GetDouble());
            }
            return result.Count > 0 ? result.ToArray() : null;
        }
    }
}
