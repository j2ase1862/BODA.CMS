namespace BODA.CMS.Collector.Storage
{
    /// <summary>배치 단위 저장소. 구현: TimescaleDB(운영) / 로그 dry-run(검증).</summary>
    public interface IFrameStore
    {
        /// <summary>스키마 준비(멱등). 시작 시 1회.</summary>
        Task InitializeAsync(CancellationToken ct);

        /// <summary>배치 적재. 실패 시 예외 — 호출자(StorageWorker)가 재시도 정책을 가진다.</summary>
        Task WriteBatchAsync(IReadOnlyList<TelemetryRecord> batch, CancellationToken ct);
    }
}
