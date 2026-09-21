using BODA.CMS.Views;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>
    /// 3D 로봇 뷰의 벤더 프로필 — 손목 구조·영점 오프셋·회전 방향(θ = offset + sign·q).
    /// 방향 값은 실기 대조로만 정한다(두산 J5 는 2026-09-21 현장 대조에서 반대로 도는 것을 확인).
    /// </summary>
    public class RobotViewProfileTests
    {
        [Fact]
        public void Doosan_wrist_pitch_rotates_opposite_to_the_reported_angle()
        {
            RobotStatusView.RobotProfile doosan = RobotStatusView.ResolveProfile("doosan");

            // J5(인덱스 4)만 반대 방향 — 펜던트가 +30° 면 모델도 같은 쪽으로 기울어야 하는데 축이 반대라 부호를 뒤집는다.
            Assert.Equal(-30, RobotStatusView.ModelAngle(doosan, 4, 30));
            Assert.Equal(45, RobotStatusView.ModelAngle(doosan, 4, -45));

            // 나머지 축은 보고값 그대로 (영점 오프셋 0)
            foreach (int axis in new[] { 0, 1, 2, 3, 5 })
                Assert.Equal(30, RobotStatusView.ModelAngle(doosan, axis, 30));
        }

        [Fact]
        public void Ur_profile_keeps_its_zero_offsets_and_does_not_flip_any_axis()
        {
            RobotStatusView.RobotProfile ur = RobotStatusView.ResolveProfile("ur");

            Assert.Equal(RobotStatusView.WristKind.OffsetPitch, ur.Wrist);
            Assert.Equal(90 + 30, RobotStatusView.ModelAngle(ur, 1, 30));    // J2 영점 +90°
            Assert.Equal(90 + 30, RobotStatusView.ModelAngle(ur, 3, 30));    // J4 영점 +90°
            Assert.Equal(30, RobotStatusView.ModelAngle(ur, 4, 30));         // 뒤집는 축 없음
            Assert.All(ur.JointSigns, s => Assert.Equal(1, s));
        }

        [Fact]
        public void Unknown_vendor_falls_back_to_the_traditional_wrist_without_guessing_a_direction()
        {
            // 미등록 벤더에 방향을 추측해 넣지 않는다 — 검증되지 않은 부호는 자세를 거꾸로 그린다.
            RobotStatusView.RobotProfile unknown = RobotStatusView.ResolveProfile("no-such-vendor");

            Assert.Equal(RobotStatusView.WristKind.RollPitchRoll, unknown.Wrist);
            Assert.All(unknown.JointSigns, s => Assert.Equal(1, s));
            Assert.All(unknown.ZeroOffsets, o => Assert.Equal(0, o));
        }

        [Fact]
        public void Jaka_is_not_flipped_until_it_is_checked_against_a_real_robot()
        {
            RobotStatusView.RobotProfile jaka = RobotStatusView.ResolveProfile("jaka");
            Assert.All(jaka.JointSigns, s => Assert.Equal(1, s));
        }

        [Fact]
        public void Model_angle_is_defined_for_axes_beyond_the_profile_tables()
        {
            RobotStatusView.RobotProfile doosan = RobotStatusView.ResolveProfile("doosan");
            Assert.Equal(15, RobotStatusView.ModelAngle(doosan, 9, 15));   // 표 밖 축은 보고값 그대로
        }
    }
}
