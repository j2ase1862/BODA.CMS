using System.Text.Json;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>수집된 프레임 + 로봇 태그 — 버퍼와 저장소를 흐르는 단위.</summary>
    public sealed record TelemetryRecord(string RobotId, RobotTelemetryFrame Frame);

    /// <summary>
    /// telemetry_frames 테이블 한 행에 대응하는 벤더 중립 값 집합.
    /// DB 드라이버(Npgsql)와 무관한 순수 변환이라 유닛테스트 대상.
    /// </summary>
    public sealed class TelemetryRow
    {
        public required DateTime TimeUtc { get; init; }
        public required string RobotId { get; init; }
        public required string Vendor { get; init; }
        public required string Channel { get; init; }
        public double? ControllerClock { get; init; }
        public required float[] PositionDeg { get; init; }
        public float[]? VelocityDegS { get; init; }
        public float[]? TorqueNm { get; init; }
        public float[]? ModelTorqueNm { get; init; }
        public float[]? ExternalTorqueNm { get; init; }
        public float[]? CurrentA { get; init; }
        public float[]? TemperatureC { get; init; }
        /// <summary>VendorRaw 딕셔너리의 JSON 직렬화(jsonb 컬럼). 원시값 없으면 null.</summary>
        public string? VendorRawJson { get; init; }

        public static TelemetryRow FromRecord(TelemetryRecord record)
        {
            RobotTelemetryFrame f = record.Frame;
            return new TelemetryRow
            {
                TimeUtc = f.ReceivedAtUtc,
                RobotId = record.RobotId,
                Vendor = f.VendorId,
                Channel = f.ChannelId,
                ControllerClock = f.ControllerClock,
                PositionDeg = f.JointPositionDeg,
                VelocityDegS = f.JointVelocityDegS,
                TorqueNm = f.JointTorqueNm,
                ModelTorqueNm = f.ModelTorqueNm,
                ExternalTorqueNm = f.ExternalTorqueNm,
                CurrentA = f.MotorCurrentA,
                TemperatureC = f.TemperatureC,
                VendorRawJson = f.VendorRaw is null ? null : JsonSerializer.Serialize(f.VendorRaw),
            };
        }
    }
}
