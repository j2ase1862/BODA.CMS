using BODA.CMS.Collector.Storage;
using BODA.CMS.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.CMS.Collector
{
    /// <summary>
    /// 구성된 로봇마다 벤더 드라이버 세트를 띄우고 채널별 수집 펌프를 돌린다.
    /// 펌프: 접속 → 프레임을 버퍼로 → Faulted 감지 시 지수 백오프 재접속 (P1 재연결 규약).
    /// <see cref="IRobotTelemetrySource"/> 계약에만 의존 — 벤더 분기 없음.
    /// </summary>
    public sealed class CollectorService : BackgroundService
    {
        private readonly IReadOnlyDictionary<string, VendorDescriptor> _catalog;
        private readonly CollectorOptions _options;
        private readonly FrameBuffer _buffer;
        private readonly ILogger<CollectorService> _logger;

        public CollectorService(
            IEnumerable<VendorDescriptor> catalog,
            IOptions<CollectorOptions> options,
            FrameBuffer buffer,
            ILogger<CollectorService> logger)
        {
            _catalog = catalog.ToDictionary(v => v.VendorId);
            _options = options.Value;
            _buffer = buffer;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            if (_options.Robots.Count == 0)
            {
                _logger.LogWarning("수집 대상 로봇이 없습니다 — appsettings.json Collector:Robots 를 구성하세요.");
                return;
            }

            var pumps = new List<Task>();
            foreach (RobotOptions robot in _options.Robots)
            {
                if (!_catalog.TryGetValue(robot.Vendor, out VendorDescriptor? vendor))
                {
                    _logger.LogError("로봇 {RobotId}: 미등록 벤더 '{Vendor}' — 카탈로그(Program.cs) 확인.", robot.RobotId, robot.Vendor);
                    continue;
                }

                IRobotTelemetrySource[] sources = vendor.CreateSources();
                foreach (IRobotTelemetrySource source in sources)
                {
                    if (robot.Channels is { Count: > 0 } filter &&
                        !filter.Contains(source.Capabilities.ChannelId, StringComparer.OrdinalIgnoreCase))
                    {
                        await source.DisposeAsync();
                        continue;
                    }
                    pumps.Add(RunPumpAsync(robot, source, ct));
                }
            }

            _logger.LogInformation("수집 펌프 {Count}개 기동 (로봇 {Robots}대).", pumps.Count, _options.Robots.Count);
            await Task.WhenAll(pumps);
        }

        private async Task RunPumpAsync(RobotOptions robot, IRobotTelemetrySource source, CancellationToken ct)
        {
            string tag = $"{robot.RobotId}/{source.Capabilities.ChannelId}";
            var endpoint = new RobotEndpoint(robot.Host, robot.Port);

            // 구독은 펌프 수명 동안 1회 — 프레임은 연결된 동안만 흐른다.
            TaskCompletionSource faulted = NewFaultSignal();
            source.FrameReceived += (_, frame) => _buffer.Post(new TelemetryRecord(robot.RobotId, frame));
            source.StateChanged += (_, state) => { if (state == TelemetrySourceState.Faulted) faulted.TrySetResult(); };
            source.Notification += (_, msg) => _logger.LogInformation("[{Tag}] {Message}", tag, msg);

            int attempt = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (faulted.Task.IsCompleted) faulted = NewFaultSignal();

                        await source.ConnectAsync(endpoint, ct);
                        attempt = 0;
                        _logger.LogInformation("[{Tag}] 연결됨 ({Host}:{Port}, {Rate}Hz).",
                            tag, endpoint.Host, endpoint.Port ?? source.Capabilities.DefaultPort,
                            source.Capabilities.NominalSampleRateHz);

                        // Faulted 또는 종료 신호까지 대기.
                        await Task.WhenAny(faulted.Task, Task.Delay(Timeout.Infinite, ct));
                        if (ct.IsCancellationRequested) break;
                        _logger.LogWarning("[{Tag}] 링크 사망 감지 — 재연결을 시작합니다.", tag);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[{Tag}] 연결 실패: {Error}", tag, ex.GetBaseException().Message);
                    }

                    await source.DisconnectAsync();

                    attempt++;
                    TimeSpan delay = BackoffPolicy.NextDelay(attempt);
                    _logger.LogInformation("[{Tag}] {Delay} 후 재연결 (시도 {Attempt}).", tag, delay, attempt);
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                await source.DisposeAsync();
                _logger.LogInformation("[{Tag}] 펌프 종료.", tag);
            }
        }

        private static TaskCompletionSource NewFaultSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
