using System;
using System.Collections.Generic;
using System.Text.Json;
using BODA.CMS.Collector;
using BODA.CMS.Collector.Storage;
using BODA.CMS.Core.Telemetry;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>수집기 순수 로직 — 프레임→행 매핑과 재연결 백오프 (P1).</summary>
    public class CollectorTests
    {
        [Fact]
        public void 프레임은_벤더_중립_행으로_매핑된다()
        {
            var frame = new RobotTelemetryFrame
            {
                ReceivedAtUtc = new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
                ControllerClock = 12.34,
                VendorId = "doosan",
                ChannelId = "drfl",
                JointPositionDeg = new float[] { 1, 2, 3, 4, 5, 6 },
                JointTorqueNm = new float[] { 10, 20, 30, 40, 50, 60 },
                MotorCurrentA = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f },
            };

            TelemetryRow row = TelemetryRow.FromRecord(new TelemetryRecord("line1-doosan-01", frame));

            Assert.Equal("line1-doosan-01", row.RobotId);
            Assert.Equal("doosan", row.Vendor);
            Assert.Equal("drfl", row.Channel);
            Assert.Equal(12.34, row.ControllerClock);
            Assert.Equal(frame.JointPositionDeg, row.PositionDeg);
            Assert.Equal(frame.JointTorqueNm, row.TorqueNm);
            Assert.Null(row.VelocityDegS);      // 미제공 신호는 null 유지 (0 채움 금지 규약)
            Assert.Null(row.TemperatureC);
            Assert.Null(row.VendorRawJson);     // 원시값 없으면 jsonb도 null
        }

        [Fact]
        public void VendorRaw는_jsonb_문자열로_보존된다()
        {
            var frame = new RobotTelemetryFrame
            {
                ReceivedAtUtc = DateTime.UtcNow,
                VendorId = "doosan",
                ChannelId = "modbus",
                JointPositionDeg = new float[6],
                VendorRaw = new Dictionary<string, double[]>
                {
                    ["cur_raw"] = new double[] { -120, 450, 0, -1, 32767, -32768 },
                },
            };

            TelemetryRow row = TelemetryRow.FromRecord(new TelemetryRecord("r1", frame));

            Assert.NotNull(row.VendorRawJson);
            var roundTrip = JsonSerializer.Deserialize<Dictionary<string, double[]>>(row.VendorRawJson!);
            Assert.Equal(frame.VendorRaw["cur_raw"], roundTrip!["cur_raw"]);
        }

        [Fact]
        public void 백오프는_지수로_늘고_30초에서_상한된다()
        {
            Assert.Equal(TimeSpan.FromSeconds(1), BackoffPolicy.NextDelay(1));
            Assert.Equal(TimeSpan.FromSeconds(2), BackoffPolicy.NextDelay(2));
            Assert.Equal(TimeSpan.FromSeconds(4), BackoffPolicy.NextDelay(3));
            Assert.Equal(TimeSpan.FromSeconds(16), BackoffPolicy.NextDelay(5));
            Assert.Equal(TimeSpan.FromSeconds(30), BackoffPolicy.NextDelay(6));   // 32s → 상한
            Assert.Equal(TimeSpan.FromSeconds(30), BackoffPolicy.NextDelay(100)); // 오버플로 없이 상한 유지
        }

        [Fact]
        public void 백오프는_0이하_시도에도_안전하다()
        {
            Assert.Equal(TimeSpan.FromSeconds(1), BackoffPolicy.NextDelay(0));
            Assert.Equal(TimeSpan.FromSeconds(1), BackoffPolicy.NextDelay(-5));
        }
    }
}
