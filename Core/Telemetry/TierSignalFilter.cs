namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 등급 하향 운용 시 프레임에서 심층 신호를 제거 — 판정 등급과 실제 수집 신호가 항상 일치하게
    /// (Basic 선택이 Pro 신호를 그대로 받는 라이선스 우회가 되지 않도록 신호도 함께 내린다).
    /// Basic = 위치 중심(§1): 전류·토크·온도를 제외하고 위치·속도만 통과.
    /// VendorRaw는 Basic 채널(두산 Modbus 등)도 제공하는 원시 보존 경로라 유지.
    /// </summary>
    public static class TierSignalFilter
    {
        public static RobotTelemetryFrame Apply(RobotTelemetryFrame f, ProductTier tier)
        {
            if (tier >= ProductTier.Pro) return f;
            if (f.MotorCurrentA is null && f.JointTorqueNm is null && f.ModelTorqueNm is null
                && f.ExternalTorqueNm is null && f.TemperatureC is null)
                return f; // 이미 Basic 신호 구성 — 복사 불필요

            return new RobotTelemetryFrame
            {
                ReceivedAtUtc = f.ReceivedAtUtc,
                ControllerClock = f.ControllerClock,
                VendorId = f.VendorId,
                ChannelId = f.ChannelId,
                JointPositionDeg = f.JointPositionDeg,
                JointVelocityDegS = f.JointVelocityDegS,
                VendorRaw = f.VendorRaw,
            };
        }
    }
}
