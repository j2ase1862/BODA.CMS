namespace BODA.CMS.Core.Telemetry
{
    /// <summary>제품 등급 (ROADMAP §1). Plus(진동+비전)는 채널 능력이 아니라 플랫폼 공통 계층에서 제공.</summary>
    public enum ProductTier
    {
        /// <summary>라이브 등급 없음 — 비패시브(오프라인 전용) 채널 또는 최소 주기 미달.</summary>
        None,
        /// <summary>범용 프로토콜 수준 모니터링: 위치 중심, ≥1Hz, 패시브.</summary>
        Basic,
        /// <summary>심층 진단: 정규화된 전류 또는 실측 토크, ≥10Hz, 패시브.</summary>
        Pro,
    }

    /// <summary>
    /// capability 선언 → 제품 등급 자동 판정 (벤더 하드코딩 금지 — ROADMAP §1).
    /// </summary>
    public static class ProductTierEvaluator
    {
        public const double BasicMinRateHz = 1.0;
        public const double ProMinRateHz = 10.0;

        public static ProductTier Evaluate(RobotCapabilities caps)
        {
            // 비개입 보장이 안 되는 채널은 라이브 등급 자체가 없다.
            if (!caps.IsPassive) return ProductTier.None;

            bool hasDeepSignal = caps.HasMotorCurrent || caps.HasJointTorqueSensor;
            if (caps.NominalSampleRateHz >= ProMinRateHz && hasDeepSignal)
                return ProductTier.Pro;

            if (caps.NominalSampleRateHz >= BasicMinRateHz)
                return ProductTier.Basic;

            return ProductTier.None;
        }
    }
}
