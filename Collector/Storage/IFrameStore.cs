using BODA.CMS.Collector.Dashboard;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>배치 단위 저장소. 구현: TimescaleDB(운영) / 로그 dry-run(검증).</summary>
    public interface IFrameStore
    {
        /// <summary>스키마 준비(멱등). 시작 시 1회.</summary>
        Task InitializeAsync(CancellationToken ct);

        /// <summary>배치 적재. 실패 시 예외 — 호출자(StorageWorker)가 재시도 정책을 가진다.</summary>
        Task WriteBatchAsync(IReadOnlyList<TelemetryRecord> batch, CancellationToken ct);

        /// <summary>CBM/ML/비전 알림 1건 저장 (저빈도 — 단건 insert). 실패 시 예외.</summary>
        Task WriteAlertAsync(AlertRecord alert, CancellationToken ct);

        /// <summary>
        /// 알림 이력 조회(최신순). 이력을 갖지 않는 구현(dry-run)은 null 을 반환하고,
        /// 호출자는 DashboardState 메모리 링으로 폴백한다. DB 미기동 등 실패는 예외.
        /// </summary>
        Task<IReadOnlyList<AlertRecord>?> QueryAlertsAsync(AlertQuery query, CancellationToken ct);
    }
}
