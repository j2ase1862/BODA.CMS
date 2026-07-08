using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Drivers.Rokae
{
    /// <summary>
    /// Rokae xCore 상태 조회 채널 드라이버 — Pro 등급 쇼케이스(전 관절 토크센서 실측 Nm).
    ///
    /// 동작: <see cref="IRokaeStateClient"/>로 접속 후 주기 폴링(기본 10Hz) → 공용 프레임 방출.
    /// 비개입: 상태 조회(읽기 전용)만 수행 — 모션 명령·제어권·RCI 미사용(§3 원칙).
    /// RCI 1kHz는 두산 RT와 동일하게 오프라인 캐릭터라이제이션 전용으로 분류(라이브 금지).
    ///
    /// capability 선언 근거: xMate 시리즈는 전 관절 토크센서 탑재(제조사 문서), SDK 상태 조회가
    /// 위치/토크/전류를 노출 — 단 실기 검증(§6 4단계) 전이므로 검증 게이트가 §5.3에 남아 있다.
    /// </summary>
    public sealed class RokaeXCoreSource : IRobotTelemetrySource
    {
        public static readonly RobotCapabilities StaticCapabilities = new()
        {
            VendorId = "rokae",
            ChannelId = "xcore-sdk",
            DisplayName = "Rokae xCore (SDK 상태 조회)",
            AxisCount = 6,                 // xMate ER 시리즈 기준
            NominalSampleRateHz = 10,      // 100ms 폴링 (SDK 비실시간 조회 — 실기에서 상한 실측)
            DefaultPort = 0,               // 포트는 SDK가 내부 관리 — 엔드포인트는 호스트만 사용
            HasJointTorqueSensor = true,   // 전 관절 토크센서 (Pro 쇼케이스) — 실기 검증 게이트 §5.3
            HasMotorCurrent = true,
            HasTemperature = true,
            IsPassive = true,              // 읽기 전용 조회 — 제어권 미취득
        };

        private const int MaxConsecutiveErrors = 10; // ~1초(100ms 폴링) 연속 실패 → 링크 사망 판정

        private readonly IRokaeStateClient _client;
        private readonly int _intervalMs;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public RokaeXCoreSource(IRokaeStateClient client, int intervalMs = 100)
        {
            _client = client;
            _intervalMs = intervalMs;
        }

        public RobotCapabilities Capabilities => StaticCapabilities;
        public TelemetrySourceState State { get; private set; } = TelemetrySourceState.Disconnected;

        public event EventHandler<RobotTelemetryFrame>? FrameReceived;
        public event EventHandler<TelemetrySourceState>? StateChanged;
        public event EventHandler<string>? Notification;

        public async Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default)
        {
            if (State == TelemetrySourceState.Connected) return;

            // 재연결 시맨틱: 이전 세션 잔재 정리 (계약 규약).
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;

            SetState(TelemetrySourceState.Connecting);
            try
            {
                await _client.ConnectAsync(endpoint.Host, ct);
            }
            catch
            {
                SetState(TelemetrySourceState.Disconnected);
                throw;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
            SetState(TelemetrySourceState.Connected);
            Notification?.Invoke(this, $"xCore 상태 조회 폴링 시작 ({1000.0 / _intervalMs:0}Hz, 읽기 전용 — 비개입).");
        }

        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            if (_loop is not null)
            {
                try { await Task.WhenAny(_loop, Task.Delay(1000)); } catch { /* 취소 예외 무시 */ }
            }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            try { await _client.DisconnectAsync(); } catch { /* 해제 실패는 무시 */ }
            SetState(TelemetrySourceState.Disconnected);
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            int consecutiveErrors = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    RokaeRobotState s = await _client.QueryStateAsync(ct);
                    consecutiveErrors = 0;

                    FrameReceived?.Invoke(this, new RobotTelemetryFrame
                    {
                        ReceivedAtUtc = DateTime.UtcNow,
                        VendorId = Capabilities.VendorId,
                        ChannelId = Capabilities.ChannelId,
                        JointPositionDeg = s.JointPositionDeg,
                        JointVelocityDegS = s.JointVelocityDegS,
                        JointTorqueNm = s.JointTorqueNm,
                        MotorCurrentA = s.MotorCurrentA,
                        TemperatureC = s.TemperatureC,
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Notification?.Invoke(this, "상태 조회 오류: " + ex.GetBaseException().Message);
                    if (++consecutiveErrors >= MaxConsecutiveErrors)
                    {
                        Notification?.Invoke(this, $"연속 조회 오류 {consecutiveErrors}회 — 링크 사망 판정, 재연결 필요.");
                        SetState(TelemetrySourceState.Faulted);
                        return;
                    }
                }

                try { await Task.Delay(_intervalMs, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private void SetState(TelemetrySourceState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            await _client.DisposeAsync();
        }
    }
}
