using System;
using System.Collections.Generic;

namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 벤더 중립 정규화 텔레메트리 프레임 — 파이프라인(저장·CBM·ML·UI)의 유일한 입력 단위.
    ///
    /// 정규화 규약 (ROADMAP §2 — 모든 드라이버가 준수):
    /// - 단위: 위치 °, 속도 °/s, 전류 A, 토크 Nm, 온도 ℃. 단위 환산은 드라이버 책임.
    /// - 스케일 미확정 신호는 환산하지 말고 <see cref="VendorRaw"/>에 원시값 그대로 보존.
    /// - 축 인덱스는 J1=0부터. 타임스탬프는 PC UTC 기준, 컨트롤러 클럭은 보조.
    /// - null 배열 = 이 채널이 해당 신호를 제공하지 않음(0 채움 금지).
    /// </summary>
    public sealed class RobotTelemetryFrame
    {
        /// <summary>PC 수신 시각(UTC).</summary>
        public required DateTime ReceivedAtUtc { get; init; }

        /// <summary>컨트롤러 내부 클럭(초). 프레임 간격·드롭 판단용 — 지원 벤더만.</summary>
        public double? ControllerClock { get; init; }

        /// <summary>벤더 식별자("doosan", "jaka", ...).</summary>
        public required string VendorId { get; init; }

        /// <summary>채널 식별자("modbus", "drfl", ...) — 같은 로봇의 다중 채널 구분.</summary>
        public required string ChannelId { get; init; }

        /// <summary>축별 관절 각도(°). 모든 채널의 필수 신호.</summary>
        public required float[] JointPositionDeg { get; init; }

        /// <summary>축별 관절 속도(°/s).</summary>
        public float[]? JointVelocityDegS { get; init; }

        /// <summary>축별 모터 전류(A). 스케일 확정 채널만 — 미확정이면 <see cref="VendorRaw"/>로.</summary>
        public float[]? MotorCurrentA { get; init; }

        /// <summary>축별 토크(Nm) — 토크센서 실측(있는 기종만).</summary>
        public float[]? JointTorqueNm { get; init; }

        /// <summary>축별 토크(Nm) — 동역학 모델 추정.</summary>
        public float[]? ModelTorqueNm { get; init; }

        /// <summary>축별 외란(외부) 토크 추정(Nm).</summary>
        public float[]? ExternalTorqueNm { get; init; }

        /// <summary>축별 온도(℃).</summary>
        public float[]? TemperatureC { get; init; }

        /// <summary>
        /// 스케일 미확정 원시값 보존 — 신호명 키(드라이버 정의) → 축별 원시값.
        /// 예: 두산 Modbus는 "temperature_raw"(300~305), "current_torque_raw"(400~405).
        /// 추정 스케일로 환산해 정규화 필드를 오염시키지 말 것. 스케일 확정 시 드라이버만 수정.
        /// </summary>
        public IReadOnlyDictionary<string, double[]>? VendorRaw { get; init; }

        public int AxisCount => JointPositionDeg.Length;
    }
}
