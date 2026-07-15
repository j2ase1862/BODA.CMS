using System;
using System.Collections.Generic;
using BODA.CMS.Core.Telemetry;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>등급 수동 선택(UI 콤보) 정책 — 상향 불가·하향 시 심층 신호 차단 (라이선스 우회 방지).</summary>
    public class TierOverrideTests
    {
        private static RobotCapabilities Caps(bool deep) => new()
        {
            VendorId = "test",
            ChannelId = "ch",
            DisplayName = "테스트",
            AxisCount = 6,
            NominalSampleRateHz = 100,
            DefaultPort = 0,
            HasMotorCurrent = deep,
            HasTemperature = deep,
            IsPassive = true,
        };

        [Fact]
        public void 자동_선택은_capability_판정_그대로다()
        {
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Effective(Caps(deep: true), null));
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Effective(Caps(deep: false), null));
        }

        [Fact]
        public void 하향_선택은_유효하다()
        {
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Effective(Caps(deep: true), ProductTier.Basic));
        }

        [Fact]
        public void 상향_선택은_capability를_넘지_못한다()
        {
            // 채널이 못 주는 신호를 등급표만 올려 팔 수 없다.
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Effective(Caps(deep: false), ProductTier.Pro));
        }

        [Fact]
        public void Basic_하향_필터는_심층_신호를_제거하고_위치_속도_원시를_보존한다()
        {
            RobotTelemetryFrame full = Frame();

            RobotTelemetryFrame filtered = TierSignalFilter.Apply(full, ProductTier.Basic);

            Assert.NotSame(full, filtered);
            Assert.Null(filtered.MotorCurrentA);
            Assert.Null(filtered.JointTorqueNm);
            Assert.Null(filtered.ModelTorqueNm);
            Assert.Null(filtered.ExternalTorqueNm);
            Assert.Null(filtered.TemperatureC);
            // 위치·속도·원시 보존 + 메타 유지
            Assert.Same(full.JointPositionDeg, filtered.JointPositionDeg);
            Assert.Same(full.JointVelocityDegS, filtered.JointVelocityDegS);
            Assert.Same(full.VendorRaw, filtered.VendorRaw);
            Assert.Equal(full.ReceivedAtUtc, filtered.ReceivedAtUtc);
            Assert.Equal(full.ControllerClock, filtered.ControllerClock);
            Assert.Equal(full.VendorId, filtered.VendorId);
            Assert.Equal(full.ChannelId, filtered.ChannelId);
        }

        [Fact]
        public void Pro_등급이면_원본을_그대로_돌려준다()
        {
            RobotTelemetryFrame full = Frame();
            Assert.Same(full, TierSignalFilter.Apply(full, ProductTier.Pro));
        }

        [Fact]
        public void 이미_Basic_신호_구성이면_복사하지_않는다()
        {
            var basic = new RobotTelemetryFrame
            {
                ReceivedAtUtc = DateTime.UtcNow,
                VendorId = "test",
                ChannelId = "ch",
                JointPositionDeg = new float[6],
                VendorRaw = new Dictionary<string, double[]> { ["raw"] = new double[6] },
            };
            Assert.Same(basic, TierSignalFilter.Apply(basic, ProductTier.Basic));
        }

        private static RobotTelemetryFrame Frame() => new()
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ControllerClock = 1.5,
            VendorId = "test",
            ChannelId = "ch",
            JointPositionDeg = new float[] { 1, 2, 3, 4, 5, 6 },
            JointVelocityDegS = new float[6],
            MotorCurrentA = new float[6],
            JointTorqueNm = new float[6],
            ModelTorqueNm = new float[6],
            ExternalTorqueNm = new float[6],
            TemperatureC = new float[6],
            VendorRaw = new Dictionary<string, double[]> { ["raw"] = new double[6] },
        };
    }
}
