using System.Net.Sockets;
using System.Text;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Drivers.Jaka
{
    /// <summary>
    /// JAKA 네이티브 모니터 채널 드라이버 — 컨트롤러가 TCP 10000으로 브로드캐스트하는
    /// 상태 JSON 스트림을 <b>수신만</b> 한다(명령 포트 10001 미사용 → 구조적 비개입·무코딩).
    ///
    /// 등급: 전류/토크/온도가 실기 검증 전(VendorRaw 보존 단계)이라 capability 규약대로
    /// Has*=false → Basic 자동 판정. §6 4단계(실기 매핑)에서 신호·스케일 확정 시
    /// 정규화 매핑 + capability 상향으로 Pro 승격 검토.
    /// </summary>
    public sealed class JakaJsonSource : IRobotTelemetrySource
    {
        public static readonly RobotCapabilities StaticCapabilities = new()
        {
            VendorId = "jaka",
            ChannelId = "monitor",
            DisplayName = "JAKA 모니터 (TCP 스트림)",
            AxisCount = 6,
            NominalSampleRateHz = 10,      // 문서 기준 ~100ms 주기 브로드캐스트
            DefaultPort = 10000,
            HasJointTorqueSensor = false,  // 실기 검증 전 — 신호는 VendorRaw로 보존 (§2 규약)
            HasMotorCurrent = false,
            HasTemperature = false,
            IsPassive = true,              // 수신 전용 스트림 — 명령 채널 미사용
        };

        private const int ConnectTimeoutMs = 3000;
        private const int IdleTimeoutMs = 5000; // 이 시간 동안 무수신이면 링크 사망 판정

        private TcpClient? _tcp;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private volatile bool _userDisconnect;

        public RobotCapabilities Capabilities => StaticCapabilities;
        public TelemetrySourceState State { get; private set; } = TelemetrySourceState.Disconnected;

        public event EventHandler<RobotTelemetryFrame>? FrameReceived;
        public event EventHandler<TelemetrySourceState>? StateChanged;
        public event EventHandler<string>? Notification;

        public async Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default)
        {
            if (State == TelemetrySourceState.Connected) return;

            // 재연결 시맨틱: 이전 세션 잔재 정리 (계약 규약).
            CleanupSession();

            SetState(TelemetrySourceState.Connecting);
            var tcp = new TcpClient();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ConnectTimeoutMs);
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port ?? Capabilities.DefaultPort, timeoutCts.Token);
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                SetState(TelemetrySourceState.Disconnected);
                throw ex is OperationCanceledException && !ct.IsCancellationRequested
                    ? new TimeoutException($"연결 타임아웃 ({ConnectTimeoutMs} ms)")
                    : ex;
            }

            _tcp = tcp;
            _userDisconnect = false;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ReadLoopAsync(tcp, _cts.Token));

            SetState(TelemetrySourceState.Connected);
            Notification?.Invoke(this, "JAKA 모니터 스트림 수신 시작 (TCP 10000, 수신 전용 — 비개입).");
        }

        public async Task DisconnectAsync()
        {
            _userDisconnect = true;
            _cts?.Cancel();
            _tcp?.Close(); // 블로킹 read 해제
            if (_loop is not null)
            {
                try { await Task.WhenAny(_loop, Task.Delay(1000)); } catch { /* 취소 예외 무시 */ }
            }
            CleanupSession();
            SetState(TelemetrySourceState.Disconnected);
        }

        private async Task ReadLoopAsync(TcpClient tcp, CancellationToken ct)
        {
            var framer = new JakaStreamFramer();
            var buffer = new byte[8192];
            long packets = 0;

            try
            {
                NetworkStream stream = tcp.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleCts.CancelAfter(IdleTimeoutMs);

                    int n;
                    try { n = await stream.ReadAsync(buffer, idleCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Notification?.Invoke(this, $"모니터 스트림 무수신 {IdleTimeoutMs / 1000}s — 링크 사망 판정.");
                        SetState(TelemetrySourceState.Faulted);
                        return;
                    }

                    if (n == 0) // 원격 종료
                    {
                        if (!_userDisconnect)
                        {
                            Notification?.Invoke(this, "컨트롤러가 스트림을 종료했습니다.");
                            SetState(TelemetrySourceState.Faulted);
                        }
                        return;
                    }

                    foreach (string json in framer.Append(Encoding.UTF8.GetString(buffer, 0, n)))
                    {
                        RobotTelemetryFrame? frame = JakaPacketParser.Parse(json, DateTime.UtcNow);
                        if (frame is null) continue;
                        if (packets++ == 0)
                            Notification?.Invoke(this, $"첫 패킷 수신 — 축 {frame.AxisCount}개, 원시 신호 {frame.VendorRaw?.Count ?? 0}종.");
                        FrameReceived?.Invoke(this, frame);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !_userDisconnect)
            {
                Notification?.Invoke(this, "스트림 오류: " + ex.GetBaseException().Message);
                SetState(TelemetrySourceState.Faulted);
            }
        }

        private void CleanupSession()
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _tcp?.Dispose();
            _tcp = null;
        }

        private void SetState(TelemetrySourceState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
