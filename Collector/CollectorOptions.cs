using BODA.CMS.Analytics;

namespace BODA.CMS.Collector
{
    /// <summary>appsettings.json "Collector" 섹션 — 다중 로봇 수집 구성 (ROADMAP §4 P1).</summary>
    public sealed class CollectorOptions
    {
        public StorageOptions Storage { get; set; } = new();
        public CbmSettings Cbm { get; set; } = new();
        public List<RobotOptions> Robots { get; set; } = new();
    }

    /// <summary>
    /// "Collector:Cbm" — 기준선 학습창을 현장 작업 특성에 맞게 조정.
    ///
    /// 기준선 학습창이 로봇 작업 1사이클보다 짧으면, 사이클 중 기준선에 안 담긴 구간이
    /// 돌아올 때마다 z≈2 수준으로 출렁여 건강도가 70~80을 오간다 — 학습창을 사이클의
    /// 몇 배로 잡아 사이클 전체 변동이 기준선 σ에 반영되게 한다.
    /// ⚠ 값을 바꾸면 ML 모델도 같은 학습창으로 재학습해야 정합이 유지된다
    ///   (앱 'AI 재학습'의 학습창 입력 또는 retrain.ps1 -LearningSeconds).
    /// </summary>
    public sealed class CbmSettings
    {
        /// <summary>로봇 작업 1사이클 길이(초). 설정하면 학습창 = CycleSeconds × CyclesToLearn.
        /// 0(기본)이면 미사용 — LearningSeconds 또는 엔진 기본(60초)을 쓴다.</summary>
        public double CycleSeconds { get; set; }

        /// <summary>기준선에 담을 사이클 수 (기본 3) — 부하가 다른 구간이 σ에 고루 반영되게.</summary>
        public int CyclesToLearn { get; set; } = 3;

        /// <summary>학습창(초) 직접 지정 — 설정하면 CycleSeconds 계산보다 우선.</summary>
        public int? LearningSeconds { get; set; }

        /// <summary>온도 경고 임계(℃) — 이 아래에서는 온도가 건강도·알림에 영향 없음. 미설정 시 엔진 기본(60).</summary>
        public double? TemperatureWarnCelsius { get; set; }

        /// <summary>온도 알람 임계(℃) — 이상 지속 시 "과열" Alarm. 미설정 시 엔진 기본(75).</summary>
        public double? TemperatureAlarmCelsius { get; set; }

        /// <summary>유효 학습창(초): 직접 지정 > 사이클×횟수 > 기본 60. 하한 30초 (집계 1초 = 1건).</summary>
        public int EffectiveLearningSeconds()
        {
            int seconds = LearningSeconds
                ?? (CycleSeconds > 0 ? (int)Math.Ceiling(CycleSeconds * Math.Max(1, CyclesToLearn)) : 60);
            return Math.Max(30, seconds);
        }

        public CbmOptions ToCbmOptions()
        {
            CbmOptions o = CbmOptions.Default with { LearningAggregates = EffectiveLearningSeconds() };
            if (TemperatureWarnCelsius is { } warn) o = o with { TemperatureWarnC = warn };
            if (TemperatureAlarmCelsius is { } alarm) o = o with { TemperatureAlarmC = alarm };
            return o;
        }
    }

    /// <summary>수집 대상 로봇 1대 — 벤더 카탈로그의 드라이버 세트가 이 엔드포인트로 붙는다.</summary>
    public sealed class RobotOptions
    {
        /// <summary>저장소 태그로 쓰는 로봇 식별자 (예: "line1-doosan-01"). 구성 내 유일해야 한다.</summary>
        public string RobotId { get; set; } = string.Empty;

        /// <summary>벤더 카탈로그 키 ("doosan", "sim", ...).</summary>
        public string Vendor { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        /// <summary>채널 공통 포트 재정의(단일 채널 벤더용). null이면 각 채널의 기본 포트.</summary>
        public int? Port { get; set; }

        /// <summary>수집할 채널 필터 (예: ["modbus"]). null/빈 목록이면 벤더의 전 채널.</summary>
        public List<string>? Channels { get; set; }
    }

    public sealed class StorageOptions
    {
        /// <summary>false면 dry-run — 적재 대신 수집률만 로그로 보고 (DB 없이 파이프라인 검증용).</summary>
        public bool Enabled { get; set; }

        public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=boda_cms;Username=postgres";

        /// <summary>이 개수가 모이면 즉시 플러시.</summary>
        public int BatchSize { get; set; } = 500;

        /// <summary>배치가 안 차도 이 주기로 플러시(ms).</summary>
        public int FlushIntervalMs { get; set; } = 1000;

        /// <summary>버퍼 상한(프레임). DB 장애 시 이 이상은 오래된 것부터 드롭 — 수집이 저장을 기다리지 않는다.</summary>
        public int BufferCapacity { get; set; } = 100_000;
    }
}
