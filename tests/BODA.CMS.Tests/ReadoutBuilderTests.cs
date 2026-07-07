using System;
using System.Collections.Generic;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.ViewModels;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>공용 판독 렌더러 — 신호 존재 여부·표시 선택 필터 규약.</summary>
    public class ReadoutBuilderTests
    {
        private static RobotTelemetryFrame Frame(float[]? current = null, Dictionary<string, double[]>? raw = null) => new()
        {
            ReceivedAtUtc = DateTime.UtcNow,
            VendorId = "test",
            ChannelId = "ch",
            JointPositionDeg = new float[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f },
            MotorCurrentA = current,
            VendorRaw = raw,
        };

        [Fact]
        public void 프레임에_없는_신호는_행을_만들지_않는다()
        {
            string outAll = TelemetrySourceViewModel.BuildReadout(Frame());

            Assert.Contains("위치°", outAll);
            Assert.DoesNotContain("전류A", outAll);   // MotorCurrentA = null
            Assert.DoesNotContain("온도℃", outAll);   // TemperatureC = null
        }

        [Fact]
        public void 선택된_신호만_행으로_출력된다()
        {
            var frame = Frame(
                current: new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f },
                raw: new Dictionary<string, double[]> { ["cur_raw"] = new double[] { 1, 2, 3, 4, 5, 6 } });

            string filtered = TelemetrySourceViewModel.BuildReadout(frame, new HashSet<string> { "위치°" });

            Assert.Contains("위치°", filtered);
            Assert.DoesNotContain("전류A", filtered);
            Assert.DoesNotContain("cur_raw", filtered);
        }

        [Fact]
        public void 전부_해제하면_안내_문구를_보여준다()
        {
            string none = TelemetrySourceViewModel.BuildReadout(Frame(), new HashSet<string>());

            Assert.Contains("선택되지 않았습니다", none);
        }

        [Fact]
        public void 필터_미지정이면_존재하는_신호를_모두_출력한다()
        {
            var frame = Frame(
                current: new float[6],
                raw: new Dictionary<string, double[]> { ["temp_raw"] = new double[6] });

            string outAll = TelemetrySourceViewModel.BuildReadout(frame);

            Assert.Contains("위치°", outAll);
            Assert.Contains("전류A", outAll);
            Assert.Contains("temp_raw", outAll);
        }

        [Fact]
        public void 헤더는_프레임의_축수를_따른다()
        {
            string outAll = TelemetrySourceViewModel.BuildReadout(Frame());

            Assert.Contains("J6", outAll);
            Assert.DoesNotContain("J7", outAll);
        }
    }
}
