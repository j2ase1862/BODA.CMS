using System;
using BODA.CMS.Drivers.Doosan;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>두산 Modbus 레지스터 → 공용 프레임 변환 (단위·축 인덱스·VendorRaw 규약).</summary>
    public class DoosanModbusFrameTests
    {
        private static ushort U(int signedValue) => unchecked((ushort)(short)signedValue);

        [Fact]
        public void 위치는_부호있는_int16에_0_1도_스케일을_적용한다()
        {
            // 실기 교정 근거: 레지스터 270 = -687 ↔ 펜던트 -68.70° (ROADMAP §5.1).
            var pos = new[] { U(-687), U(0), U(123), U(-1), U(1800), U(-1800) };
            var frame = DoosanModbusSource.ToFrame(DateTime.UtcNow, pos, new ushort[6], new ushort[6]);

            Assert.Equal(-68.7f, frame.JointPositionDeg[0], precision: 3);
            Assert.Equal(0f, frame.JointPositionDeg[1]);
            Assert.Equal(12.3f, frame.JointPositionDeg[2], precision: 3);
            Assert.Equal(-0.1f, frame.JointPositionDeg[3], precision: 3);
            Assert.Equal(180f, frame.JointPositionDeg[4], precision: 3);
            Assert.Equal(-180f, frame.JointPositionDeg[5], precision: 3);
        }

        [Fact]
        public void 스케일_미확정_신호는_정규화_필드가_아니라_VendorRaw에_보존된다()
        {
            var temp = new[] { U(31), U(32), U(30), U(29), U(33), U(31) };
            var cur = new[] { U(-120), U(450), U(0), U(-1), U(32767), U(-32768) };
            var frame = DoosanModbusSource.ToFrame(DateTime.UtcNow, new ushort[6], temp, cur);

            // 정규화 필드는 비어 있어야 한다 — 추정 스케일로 오염 금지(§2 규약).
            Assert.Null(frame.TemperatureC);
            Assert.Null(frame.MotorCurrentA);
            Assert.Null(frame.JointTorqueNm);

            // 원시값은 부호 복원까지만 하고 그대로 보존.
            Assert.NotNull(frame.VendorRaw);
            Assert.Equal(new double[] { 31, 32, 30, 29, 33, 31 }, frame.VendorRaw![DoosanModbusSource.RawTemperatureKey]);
            Assert.Equal(new double[] { -120, 450, 0, -1, 32767, -32768 }, frame.VendorRaw![DoosanModbusSource.RawCurrentTorqueKey]);
        }

        [Fact]
        public void 프레임은_벤더_채널_태그와_축수를_보고한다()
        {
            var frame = DoosanModbusSource.ToFrame(DateTime.UtcNow, new ushort[6], new ushort[6], new ushort[6]);

            Assert.Equal("doosan", frame.VendorId);
            Assert.Equal("modbus", frame.ChannelId);
            Assert.Equal(6, frame.AxisCount);
        }
    }
}
