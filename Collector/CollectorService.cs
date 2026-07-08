using BODA.CMS.Analytics;
using BODA.CMS.Analytics.Ml;
using BODA.CMS.Collector.Dashboard;
using BODA.CMS.Collector.Storage;
using BODA.CMS.Core.Licensing;
using BODA.CMS.Core.Telemetry;
using Microsoft.Extensions.Options;

namespace BODA.CMS.Collector
{
    /// <summary>
    /// 구성된 로봇마다 벤더 드라이버 세트를 띄우고 채널별 수집 펌프를 돌린다.
    /// 펌프: 접속 → 프레임을 버퍼로 + CBM/ML 무인 감시 → Faulted 감지 시 지수 백오프 재접속.
    /// 알림은 대시보드 링 + 저장소로. 라이선스 등급을 넘는 채널은 기동하지 않는다 (P5).
    /// <see cref="IRobotTelemetrySource"/> 계약에만 의존 — 벤더 분기 없음.
    /// </summary>
    public sealed class CollectorService : BackgroundService
    {
        private readonly IReadOnlyDictionary<string, VendorDescriptor> _catalog;
        private readonly CollectorOptions _options;
        private readonly FrameBuffer _buffer;
        private readonly IFrameStore _store;
        private readonly DashboardState _dashboard;
        private readonly LicenseStatus _license;
        private readonly ILogger<CollectorService> _logger;

        public CollectorService(
            IEnumerable<VendorDescriptor> catalog,
            IOptions<CollectorOptions> options,
            FrameBuffer buffer,
            IFrameStore store,
            DashboardState dashboard,
            LicenseStatus license,
            ILogger<CollectorService> logger)
        {
            _catalog = catalog.ToDictionary(v => v.VendorId);
            _options = options.Value;
            _buffer = buffer;
            _store = store;
            _dashboard = dashboard;
            _license = license;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("{License}", _license.Description);

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

                    // 라이선스 게이팅 — 등급은 capability 자동 판정(ROADMAP §1)과 연동.
                    ProductTier tier = ProductTierEvaluator.Evaluate(source.Capabilities);
                    if (!_license.AllowsChannel(tier))
                    {
                        _logger.LogWarning("[{Robot}/{Channel}] 라이선스 등급 초과({Tier}) — 채널을 기동하지 않습니다.",
                            robot.RobotId, source.Capabilities.ChannelId, tier);
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
            // 즉시 스레드풀로 양보 — 아래 ONNX 세션 로드(콜드 스타트 10초+ 실측)가
            // StartAsync를 막아 Kestrel(웹 대시보드) 바인딩과 서비스 기동을 지연시키지 않게 한다.
            await Task.Yield();

            string channel = source.Capabilities.ChannelId;
            string tag = $"{robot.RobotId}/{channel}";
            var endpoint = new RobotEndpoint(robot.Host, robot.Port);

            // 무인 감시: 채널당 CBM + (모델 있으면) ML — WPF와 동일 파이프라인 (P2/P3의 P5 통합).
            var cbm = new CbmMonitor();
            MlAnomalyMonitor? ml = MlAnomalyMonitor.TryLoad(Path.Combine(AppContext.BaseDirectory, "models"));
            ml?.Attach(cbm);
            _dashboard.Register(robot.RobotId, source, cbm, ml);

            // 구독은 펌프 수명 동안 1회 — 프레임은 연결된 동안만 흐른다.
            TaskCompletionSource faulted = NewFaultSignal();
            source.FrameReceived += (_, frame) =>
            {
                _buffer.Post(new TelemetryRecord(robot.RobotId, frame));
                cbm.Ingest(frame);
                _dashboard.OnFrame(robot.RobotId, channel, frame.ReceivedAtUtc);
            };
            cbm.AlertRaised += a => HandleAlert(robot.RobotId, tag, a);
            if (ml is not null) ml.AlertRaised += a => HandleAlert(robot.RobotId, tag, a);
            source.StateChanged += (_, state) =>
            {
                _dashboard.SetState(robot.RobotId, channel, state);
                if (state == TelemetrySourceState.Faulted) faulted.TrySetResult();
            };
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
                ml?.Dispose();
                _logger.LogInformation("[{Tag}] 펌프 종료.", tag);
            }
        }

        private void HandleAlert(string robotId, string tag, CbmAlert alert)
        {
            var record = AlertRecord.From(robotId, alert);
            _dashboard.AddAlert(record);
            _logger.LogWarning("[{Tag}] {Severity} {Message}", tag, alert.Severity, alert.Message);

            // 저장은 수집 경로를 막지 않게 비동기 최선 노력 (실패는 로그만 — 대시보드 링에는 남아 있음).
            _ = Task.Run(async () =>
            {
                try { await _store.WriteAlertAsync(record, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "알림 저장 실패: {Message}", record.Message); }
            });
        }

        private static TaskCompletionSource NewFaultSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
