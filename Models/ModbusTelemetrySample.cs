using System;

namespace DoosanMonitor.Models
{
    /// <summary>
    /// Modbus(FC03) 한 폴링 사이클의 축별 진단 스냅샷.
    /// 실측 매핑(CS-01, DRCF V2.11.1, unit=1): 위치 270~275, 온도 300~305, 전류/토크 400~405.
    /// </summary>
    public sealed class ModbusTelemetrySample
    {
        public DateTime ReceivedAt { get; init; }

        /// <summary>축별 관절 각도(°). 레지스터 270~275 × 0.1 (스케일 실측 확정).</summary>
        public float[] JointPositionDeg { get; init; } = Array.Empty<float>();

        /// <summary>축별 온도(℃ 추정). 레지스터 300~305 (int16 원시값, 스케일 ≈1℃/count).</summary>
        public short[] Temperature { get; init; } = Array.Empty<short>();

        /// <summary>축별 전류/토크(원시 int16). 레지스터 400~405. 스케일 미확정 → 원시값 보존.</summary>
        public short[] CurrentTorqueRaw { get; init; } = Array.Empty<short>();

        public int AxisCount => JointPositionDeg.Length;
    }
}
