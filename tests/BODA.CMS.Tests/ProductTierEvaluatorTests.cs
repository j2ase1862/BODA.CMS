using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>capability 선언 → 등급 자동 판정 (ROADMAP §1 / P0 게이트).</summary>
    public class ProductTierEvaluatorTests
    {
        private static RobotCapabilities Caps(
            double rateHz, bool passive = true, bool current = false, bool jts = false) => new()
        {
            VendorId = "test",
            ChannelId = "ch",
            DisplayName = "테스트",
            AxisCount = 6,
            NominalSampleRateHz = rateHz,
            DefaultPort = 1,
            HasMotorCurrent = current,
            HasJointTorqueSensor = jts,
            IsPassive = passive,
        };

        [Fact]
        public void 비패시브_채널은_라이브_등급이_없다()
        {
            // RT 제어처럼 로봇을 장악하는 채널 — 심층 신호가 있어도 라이브 경로 금지(§3 비개입 원칙).
            Assert.Equal(ProductTier.None, ProductTierEvaluator.Evaluate(Caps(1000, passive: false, current: true, jts: true)));
        }

        [Fact]
        public void 패시브_1Hz_이상이면_Basic()
        {
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Evaluate(Caps(1)));
        }

        [Fact]
        public void 패시브라도_1Hz_미만이면_None()
        {
            Assert.Equal(ProductTier.None, ProductTierEvaluator.Evaluate(Caps(0.5)));
        }

        [Fact]
        public void 정규화_전류가_있고_10Hz_이상이면_Pro()
        {
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Evaluate(Caps(10, current: true)));
        }

        [Fact]
        public void 실측_토크센서만으로도_Pro_성립()
        {
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Evaluate(Caps(100, jts: true)));
        }

        [Fact]
        public void 심층_신호가_있어도_10Hz_미만이면_Basic에_머문다()
        {
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Evaluate(Caps(5, current: true)));
        }

        [Fact]
        public void 심층_신호_없이_10Hz면_Basic()
        {
            // 두산 Modbus 케이스: 전류/토크가 VendorRaw에만 있어 Has*는 false → Basic.
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Evaluate(Caps(10)));
        }

        // ── 실제 두산 드라이버 선언이 의도한 등급으로 판정되는지 잠금 ──

        [Fact]
        public void 두산_Modbus_채널은_Basic()
        {
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Evaluate(DoosanModbusSource.StaticCapabilities));
        }

        [Fact]
        public void 두산_DRFL_채널은_Pro()
        {
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Evaluate(DoosanDrflSource.StaticCapabilities));
        }

        [Fact]
        public void 두산_Modbus는_스케일_미확정_신호를_capability로_선언하지_않는다()
        {
            // 규약(§2): Has*는 정규화 완료 신호만 true. 400~405/300~305는 스케일 미확정 → false 유지.
            // 스케일 확정 시 드라이버에서 플래그·매핑을 함께 올리면 이 테스트를 갱신한다.
            Assert.False(DoosanModbusSource.StaticCapabilities.HasMotorCurrent);
            Assert.False(DoosanModbusSource.StaticCapabilities.HasTemperature);
        }
    }
}
