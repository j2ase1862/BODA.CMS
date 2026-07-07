namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 드라이버(채널)가 선언하는 능력 — 제품 등급 자동 판정(<see cref="ProductTierEvaluator"/>)의 근거.
    ///
    /// ⚠️ Has* 플래그는 <b>정규화 완료된 신호만</b> true로 선언한다 (ROADMAP §2).
    ///    VendorRaw에만 있는 스케일 미확정 신호는 false — 스케일 확정 시 드라이버가
    ///    플래그와 매핑을 함께 올리고, 등급 판정도 그때 상향된다.
    /// </summary>
    public sealed class RobotCapabilities
    {
        /// <summary>벤더 식별자("doosan", "jaka", ...).</summary>
        public required string VendorId { get; init; }

        /// <summary>채널 식별자("modbus", "drfl", ...).</summary>
        public required string ChannelId { get; init; }

        /// <summary>UI 표시용 이름 (예: "두산 Modbus").</summary>
        public required string DisplayName { get; init; }

        /// <summary>축 수 (H2017 = 6).</summary>
        public required int AxisCount { get; init; }

        /// <summary>공칭 샘플링 주기(Hz).</summary>
        public required double NominalSampleRateHz { get; init; }

        /// <summary>이 채널의 기본 접속 포트.</summary>
        public required int DefaultPort { get; init; }

        /// <summary>토크센서 실측 토크 제공 (Rokae 전축 O, 두산 JTS 기종 O).</summary>
        public bool HasJointTorqueSensor { get; init; }

        /// <summary>정규화된 모터 전류(A) 제공.</summary>
        public bool HasMotorCurrent { get; init; }

        /// <summary>정규화된 온도(℃) 제공.</summary>
        public bool HasTemperature { get; init; }

        /// <summary>
        /// 비개입 보장 채널인가 — 라이브 감시 허용 조건 (ROADMAP §3 비개입 원칙).
        /// false인 채널(RT 제어 등)은 오프라인 캐릭터라이제이션 전용.
        /// </summary>
        public required bool IsPassive { get; init; }
    }
}
