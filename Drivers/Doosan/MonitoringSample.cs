using System;

namespace BODA.CMS.Drivers.Doosan
{
    /// <summary>
    /// DRFL 모니터링 콜백 한 프레임을 진단에 필요한 축별 값만 추려 담은 불변 스냅샷.
    /// ⚠️ 드라이버 내부 타입 — 파이프라인 입력 단위는 Core의 RobotTelemetryFrame이다(P0에서 강등).
    /// </summary>
    public sealed class MonitoringSample
    {
        /// <summary>수신 시각(PC 기준). 컨트롤러 내부 클럭은 <see cref="SyncTime"/>.</summary>
        public DateTime ReceivedAt { get; init; }

        /// <summary>컨트롤러 내부 동기 클럭(초). 프레임 간격·드롭 판단용.</summary>
        public double SyncTime { get; init; }

        // 축별(길이 = 축 수, H2017 = 6). 모두 컨트롤러 원본 단위.
        public float[] JointPosition { get; init; } = Array.Empty<float>();   // deg
        public float[] JointVelocity { get; init; } = Array.Empty<float>();   // deg/s
        public float[] DynamicTorque { get; init; } = Array.Empty<float>();   // 동역학 모델 토크
        public float[] JointTorqueSensor { get; init; } = Array.Empty<float>(); // 관절토크센서 실측
        public float[] ExternalJointTorque { get; init; } = Array.Empty<float>(); // 외부 관절토크
        public float[] MotorCurrent { get; init; } = Array.Empty<float>();    // 모터 입력 전류
        public float[] MotorTemperature { get; init; } = Array.Empty<float>(); // 모터 온도

        public int AxisCount => JointPosition.Length;
    }
}
