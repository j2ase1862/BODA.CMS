using System.Windows.Media.Media3D;
using BODA.CMS.Views;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>
    /// 3D 로봇 뷰의 **자세** 검증 — 부호 산술(<see cref="RobotViewProfileTests"/>)이 아니라 팔·공구가 실제로
    /// 어디를 향하는지 본다. v0.7.7 의 두산 J5 반전은 ModelAngle 테스트를 통과하면서도 손목을 반대로 그렸다:
    /// 부호만 보는 테스트로는 자세 오류를 못 잡는다.
    ///
    /// 모델과 같은 표(<c>ChainFor</c>)·같은 변환 합성(<c>JointTransform</c>)을 써서 플랜지까지 정운동학을 푼다.
    /// 모델 좌표계는 Y-up(바닥 원판 y=0).
    /// </summary>
    public class RobotViewPostureTests
    {
        /// <summary>두산 실기 2026-09-21: 펜던트 조인트 값. 태스크 Z=1074.36 에서 손목이 아래로 꺾여 있었다.</summary>
        private static readonly double[] DoosanElbowDownQ = { 0.01, 0.07, -89.93, 0.01, -90.03, -0.01 };

        private sealed record Posture(Point3D Elbow, Point3D Flange, Vector3D ToolDirection);

        private static Posture Solve(RobotStatusView.RobotProfile profile, double[] q)
        {
            RobotStatusView.JointFrame[] chain = RobotStatusView.ChainFor(profile.Wrist);
            var cumulative = Matrix3D.Identity;

            void Apply(RobotStatusView.JointFrame frame, double angleDeg) =>
                cumulative = Matrix3D.Multiply(
                    RobotStatusView.JointTransform(frame, new AxisAngleRotation3D(frame.Axis, angleDeg)).Value,
                    cumulative);

            // 루트 베이스 요(카메라 정렬용) 부터 — 형상엔 영향이 없지만 모델과 같은 순서로 푼다.
            cumulative = Matrix3D.Multiply(
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), RobotStatusView.BaseYawDeg)).Value,
                cumulative);

            Point3D elbow = default;
            for (int axis = 0; axis < chain.Length; axis++)
            {
                Apply(chain[axis], RobotStatusView.ModelAngle(profile, axis, q[axis]));
                if (axis == 2) elbow = cumulative.Transform(new Point3D(0, 0, 0)); // J3 원점 = 팔꿈치
            }

            Point3D flange = cumulative.Transform(new Point3D(0, 0, 0));
            Point3D tip = cumulative.Transform(new Point3D(0, 0, 0) + RobotStatusView.ToolTipOffset(profile.Wrist));
            Vector3D dir = tip - flange;
            dir.Normalize();
            return new Posture(elbow, flange, dir);
        }

        [Fact]
        public void Doosan_at_all_zero_stands_straight_up()
        {
            // 두산 영점 규약: 0° = 수직 상향. 롤-피치-롤 손목이라 플랜지까지 일직선이다.
            RobotStatusView.RobotProfile doosan = RobotStatusView.ResolveProfile("doosan");
            Posture p = Solve(doosan, new double[6]);

            Assert.True(p.Flange.Y > p.Elbow.Y, "플랜지가 팔꿈치보다 위여야 한다");
            Assert.True(p.ToolDirection.Y > 0.99, $"공구축이 위를 향해야 한다 ({p.ToolDirection})");
        }

        [Fact]
        public void Doosan_wrist_bent_to_minus_90_points_the_tool_down()
        {
            RobotStatusView.RobotProfile doosan = RobotStatusView.ResolveProfile("doosan");
            Posture p = Solve(doosan, DoosanElbowDownQ);

            // 실기: 상완 수직 → 팔꿈치 90° → 전완 수평 → 손목이 **아래로** 꺾여 그리퍼가 바닥을 본다.
            // J5 부호를 뒤집으면(v0.7.7) 공구축이 +Y 로 뒤집혀 이 단정이 깨진다.
            Assert.True(p.ToolDirection.Y < -0.99, $"공구축이 아래를 향해야 한다 ({p.ToolDirection})");
            Assert.True(p.Flange.Y < p.Elbow.Y, "플랜지가 전완 평면(팔꿈치 높이)보다 아래여야 한다");
        }

        [Fact]
        public void Doosan_base_yaw_turns_the_arm_the_same_way_the_pendant_reports()
        {
            // 실기 확인(2026-09-21): J1 을 +90° 조그하니 3D 도 같은 방향으로 돌았다.
            // 부호표뿐 아니라 J1 축 벡터까지 고정한다 — 축을 (0,-1,0) 으로 바꿔도 부호 테스트는 통과한다.
            RobotStatusView.RobotProfile doosan = RobotStatusView.ResolveProfile("doosan");

            double Azimuth(double q1)
            {
                double[] q = (double[])DoosanElbowDownQ.Clone();
                q[0] = q1;
                Posture p = Solve(doosan, q);
                return Math.Atan2(p.Flange.X, p.Flange.Z) * 180 / Math.PI; // +Y 축 오른손 회전 = 방위각 증가
            }

            double turned = (Azimuth(90) - Azimuth(0) + 540) % 360 - 180; // (-180, 180] 로 정규화
            Assert.InRange(turned, 89, 91);
        }

        [Fact]
        public void Ur_zero_offsets_lay_the_arm_out_horizontally()
        {
            // UR 은 q=0 에서 팔이 수평 — 영점 오프셋(J2·J4 +90°)이 살아 있는지 자세로 확인한다.
            RobotStatusView.RobotProfile ur = RobotStatusView.ResolveProfile("ur");
            Posture p = Solve(ur, new double[6]);

            Assert.True(p.Elbow.Y > 0.1, "팔꿈치는 베이스 위에 있어야 한다");
            // 전완이 수평 — 플랜지가 팔꿈치와 거의 같은 높이에서 멀리 뻗는다(d6 측면 링크만큼만 내려감).
            Assert.InRange(p.Flange.Y - p.Elbow.Y, -0.07, 0.01);
            double reach = new Vector3D(p.Flange.X - p.Elbow.X, 0, p.Flange.Z - p.Elbow.Z).Length;
            Assert.True(reach > 0.2, $"전완이 수평으로 뻗어야 한다 (reach={reach:F3})");
        }
    }
}
