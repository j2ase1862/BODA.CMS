using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>
    /// 버퍼를 배치로 묶어 저장소에 플러시하는 워커: BatchSize가 차거나 FlushInterval이 지나면 적재.
    /// 적재 실패 시 배치를 버리지 않고 백오프 후 재시도 — 그동안 버퍼가 차면 오래된 프레임부터 드롭.
    /// </summary>
    public sealed class StorageWorker : BackgroundService
    {
        private readonly FrameBuffer _buffer;
        private readonly IFrameStore _store;
        private readonly StorageOptions _options;
        private readonly ILogger<StorageWorker> _logger;

        public StorageWorker(FrameBuffer buffer, IFrameStore store, IOptions<CollectorOptions> options, ILogger<StorageWorker> logger)
        {
            _buffer = buffer;
            _store = store;
            _options = options.Value.Storage;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // 초기화 실패(DB 미기동·미설치)가 호스트를 내리지 않도록 백오프 재시도 —
            // 서비스가 설치 직후 자동 시작되므로 DB가 늦게 준비되는 상황이 정상 경로다.
            for (int initTries = 1; ; initTries++)
            {
                try { await _store.InitializeAsync(ct); break; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    TimeSpan delay = BackoffPolicy.NextDelay(initTries);
                    _logger.LogError(ex, "저장소 초기화 실패({Tries}회) — {Delay} 후 재시도 (수집은 계속, 버퍼 한도 초과분은 드롭).",
                        initTries, delay);
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                }
            }

            var batch = new List<TelemetryRecord>(_options.BatchSize);
            var flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMs);
            int failStreak = 0;
            long lastDropReport = 0;

            while (!ct.IsCancellationRequested)
            {
                // 배치 채우기: BatchSize 또는 FlushInterval 중 먼저 도달하는 쪽까지.
                DateTime deadline = DateTime.UtcNow + flushInterval;
                while (batch.Count < _options.BatchSize && DateTime.UtcNow < deadline)
                {
                    TimeSpan remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(remaining);
                    try
                    {
                        TelemetryRecord record = await _buffer.Reader.ReadAsync(readCts.Token);
                        batch.Add(record);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break; // 플러시 주기 도달
                    }
                }

                if (batch.Count == 0) continue;

                try
                {
                    await _store.WriteBatchAsync(batch, ct);
                    batch.Clear();
                    failStreak = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    failStreak++;
                    TimeSpan delay = BackoffPolicy.NextDelay(failStreak);
                    _logger.LogError(ex, "적재 실패({Streak}회) — 배치 {Count}건 보존, {Delay} 후 재시도.",
                        failStreak, batch.Count, delay);
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                }

                long dropped = _buffer.DroppedCount;
                if (dropped > lastDropReport)
                {
                    _logger.LogWarning("버퍼 포화로 누적 {Dropped}프레임 드롭됨 (저장 지연).", dropped);
                    lastDropReport = dropped;
                }
            }

            // 종료 시 잔여 배치 최선 노력 플러시.
            if (batch.Count > 0)
            {
                try { await _store.WriteBatchAsync(batch, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "종료 플러시 실패 — {Count}건 유실.", batch.Count); }
            }
        }
    }
}
