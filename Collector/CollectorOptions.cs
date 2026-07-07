namespace BODA.CMS.Collector
{
    /// <summary>appsettings.json "Collector" 섹션 — 다중 로봇 수집 구성 (ROADMAP §4 P1).</summary>
    public sealed class CollectorOptions
    {
        public StorageOptions Storage { get; set; } = new();
        public List<RobotOptions> Robots { get; set; } = new();
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
