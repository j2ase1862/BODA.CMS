using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Analytics
{
    /// <summary>CBM 판정 파라미터. 기본값은 10~100Hz 스트림·1초 집계 기준의 보수적 설정.</summary>
    public sealed record CbmOptions
    {
        public static CbmOptions Default { get; } = new();

        /// <summary>집계 창(초) — 프레임을 이 단위 평균으로 눌러서 판정(고주파 노이즈 억제).</summary>
        public int AggregationSeconds { get; init; } = 1;

        /// <summary>신호·축별 기준선 학습에 쓰는 집계 수 (기본 60 = 약 1분). 학습 중에는 알림 없음.</summary>
        public int LearningAggregates { get; init; } = 60;

        /// <summary>급변(스파이크) 판정 z-임계. |z| ≥ 이 값이 연속 <see cref="SpikeDebounce"/>회면 Alarm.</summary>
        public double SpikeZ { get; init; } = 4.0;
        public int SpikeDebounce { get; init; } = 3;

        /// <summary>드리프트(추세) 판정 — EWMA(지수평활)의 z가 연속 <see cref="DriftDebounce"/>회 초과하면 Warning.</summary>
        public double DriftZ { get; init; } = 3.0;
        public int DriftDebounce { get; init; } = 10;
        public double EwmaAlpha { get; init; } = 0.1;

        /// <summary>해제: 활성 알림 상태에서 |z|가 이 값 밑으로 연속 <see cref="ResolveDebounce"/>회면 복귀 통지.</summary>
        public double ResolveZ { get; init; } = 1.5;
        public int ResolveDebounce { get; init; } = 5;

        /// <summary>기준선 σ 하한 — 거의 상수인 신호가 미세 잡음에 과민 알람하는 것을 막는다.
        /// 실효 σ = max(σ, AbsoluteMinStd, RelativeMinStd × |μ|).</summary>
        public double AbsoluteMinStd { get; init; } = 1e-3;
        public double RelativeMinStd { get; init; } = 0.01;

        /// <summary>감시 제외 신호 — 위치/속도는 동작 의존이라 건강 지표가 아니다.</summary>
        public IReadOnlySet<string> ExcludedSignals { get; init; } =
            new HashSet<string> { TelemetrySignals.PositionLabel, TelemetrySignals.VelocityLabel };

        /// <summary>온도 경고 임계(℃) — 이 아래에서는 온도가 건강도·알림에 전혀 영향을 주지 않는다.
        /// 온도는 웜업(기동 후 수십 분 상승)이 정상 거동이라 기준선 z 판정이 부적합해,
        /// 온도 신호만 절대 임계로 판정한다: 경고 아래 100점 ↔ 알람에서 0점 선형.</summary>
        public double TemperatureWarnC { get; init; } = 60;

        /// <summary>온도 알람 임계(℃) — 이 이상이 디바운스 지속되면 "과열" Alarm.</summary>
        public double TemperatureAlarmC { get; init; } = 75;
    }
}
