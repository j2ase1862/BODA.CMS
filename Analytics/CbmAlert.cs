namespace BODA.CMS.Analytics
{
    public enum CbmSeverity
    {
        /// <summary>정보성 통지(복귀 등).</summary>
        Info,
        /// <summary>주의 — 드리프트(추세성 이탈).</summary>
        Warning,
        /// <summary>경보 — 급변(스파이크).</summary>
        Alarm,
    }

    public enum CbmPhase { Learning, Monitoring }

    /// <summary>CBM 알림 1건 — 신호·축 단위.</summary>
    public sealed record CbmAlert(
        DateTime AtUtc,
        string VendorId,
        string ChannelId,
        string Signal,
        int AxisIndex,
        CbmSeverity Severity,
        string Kind,            // "급변" | "드리프트" | "복귀"
        double Z,
        double BaselineMean,
        double BaselineStd,
        double Value)
    {
        public string Message =>
            $"J{AxisIndex + 1} {Signal} {Kind}: z={Z:0.0} (기준 {BaselineMean:0.###}±{BaselineStd:0.###}, 현재 {Value:0.###})";
    }

    /// <summary>UI 폴링용 현재 상태 스냅샷.</summary>
    public sealed record CbmSnapshot(
        CbmPhase Phase,
        double LearningProgress,   // 0..1 (전 신호·축 평균)
        int HealthScore,           // 0..100 — 최악 |z| 기반 휴리스틱 (z≤1 → 100, z≥5 → 0)
        int ActiveAlertCount,
        int MonitoredCount,        // 감시 중인 신호·축 수
        string? WorstDescription); // 예: "J3 전류A z=4.2"
}
