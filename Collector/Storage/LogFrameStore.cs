using Microsoft.Extensions.Logging;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>
    /// dry-run 저장소 (Storage:Enabled=false) — 적재 대신 로봇/채널별 수집률만 로그.
    /// DB 없이 수집 파이프라인(드라이버→펌프→버퍼→배치)을 검증하는 용도.
    /// </summary>
    public sealed class LogFrameStore : IFrameStore
    {
        private readonly ILogger<LogFrameStore> _logger;
        private readonly Dictionary<string, long> _counts = new();

        public LogFrameStore(ILogger<LogFrameStore> logger) => _logger = logger;

        public Task InitializeAsync(CancellationToken ct)
        {
            _logger.LogWarning("Storage:Enabled=false — dry-run 모드. 프레임은 적재되지 않고 수집률만 보고합니다.");
            return Task.CompletedTask;
        }

        public Task WriteBatchAsync(IReadOnlyList<TelemetryRecord> batch, CancellationToken ct)
        {
            foreach (TelemetryRecord r in batch)
            {
                string key = $"{r.RobotId}/{r.Frame.ChannelId}";
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }

            _logger.LogInformation("dry-run 플러시: 배치 {Batch}건 — 누적 {Summary}",
                batch.Count, string.Join(", ", _counts.Select(kv => $"{kv.Key}={kv.Value}")));
            return Task.CompletedTask;
        }
    }
}
