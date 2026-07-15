using System.Net.Sockets;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Drivers.UR
{
    /// <summary>
    /// Universal Robots RTDE 채널 드라이버 — 컨트롤러(TCP 30004)에 출력 레시피를 구독하고
    /// 바이너리 스트림을 <b>수신만</b> 한다(입력 레시피·명령 미사용 → 구조적 비개입·무코딩).
    ///
    /// 등급: RTDE 문서가 단위를 명세(actual_current=A, joint_temperatures=℃)하므로 Rokae 전례대로
    /// capability true 선언 → Pro 자동 판정. 단 실기 검증(§6 4단계) 게이트가 §5.5에 남는다.
    /// UR은 관절 토크센서 미탑재 — target_moment(목표 토크)를 모델 토크로 매핑, 실측 토크는 없음.
    ///
    /// 단위 정규화: RTDE는 라디안 기준(actual_q rad, actual_qd rad/s) — 드라이버가 °/°/s로 환산(§2 규약).
    /// </summary>
    public sealed class UrRtdeSource : IRobotTelemetrySource
    {
        public static readonly RobotCapabilities StaticCapabilities = new()
        {
            VendorId = "ur",
            ChannelId = "rtde",
            DisplayName = "UR RTDE (출력 구독)",
            AxisCount = 6,
            NominalSampleRateHz = 125,     // CB3 상한 — e-Series는 500Hz까지, 실기 확인 후 상향 검토
            DefaultPort = 30004,
            HasJointTorqueSensor = false,  // UR 전 기종 관절 토크센서 미탑재 (e-Series는 TCP F/T만)
            HasMotorCurrent = true,        // actual_current (A — RTDE 문서 명세)
            HasTemperature = true,         // joint_temperatures (℃)
            IsPassive = true,              // 출력 구독 전용 — 입력 레시피·스크립트 전송 미사용
        };

        // 출력 레시피 — 전 변수가 double 계열(DOUBLE 1 + VECTOR6D 5 = double 31개/패키지).
        internal static readonly string[] OutputVariables =
            { "timestamp", "actual_q", "actual_qd", "actual_current", "target_moment", "joint_temperatures" };
        internal static readonly string[] ExpectedTypes =
            { "DOUBLE", "VECTOR6D", "VECTOR6D", "VECTOR6D", "VECTOR6D", "VECTOR6D" };
        private const int ValuesPerFrame = 31;

        private const double RequestedFrequencyHz = 125;
        private const int ConnectTimeoutMs = 3000;
        private const int HandshakeStepTimeoutMs = 3000;
        private const int IdleTimeoutMs = 5000; // 이 시간 동안 무수신이면 링크 사망 판정
        private const double RadToDeg = 180.0 / Math.PI;

        private TcpClient? _tcp;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private volatile bool _userDisconnect;
        private byte _recipeId;

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

                // 버전 협상 → 출력 구독 → 시작. 실패는 예외로 — 상태는 Disconnected로 남는다.
                var framer = new RtdeFramer();
                var backlog = new Queue<(byte Type, byte[] Payload)>();
                await HandshakeAsync(tcp.GetStream(), framer, backlog, ct);

                _tcp = tcp;
                _userDisconnect = false;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => ReadLoopAsync(tcp, framer, backlog, _cts.Token));
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                SetState(TelemetrySourceState.Disconnected);
                throw ex is OperationCanceledException && !ct.IsCancellationRequested
                    ? new TimeoutException($"RTDE 연결/핸드셰이크 타임아웃 ({ConnectTimeoutMs} ms)")
                    : ex;
            }

            SetState(TelemetrySourceState.Connected);
            Notification?.Invoke(this,
                $"RTDE 출력 구독 시작 ({RequestedFrequencyHz:0}Hz 요청, 수신 전용 — 비개입). 레시피 id={_recipeId}.");
        }

        public async Task DisconnectAsync()
        {
            _userDisconnect = true;
            _cts?.Cancel();
            _tcp?.Close(); // 블로킹 read 해제 — 연결 종료로 컨트롤러 송신도 멈춘다
            if (_loop is not null)
            {
                try { await Task.WhenAny(_loop, Task.Delay(1000)); } catch { /* 취소 예외 무시 */ }
            }
            CleanupSession();
            SetState(TelemetrySourceState.Disconnected);
        }

        private async Task HandshakeAsync(NetworkStream stream, RtdeFramer framer,
            Queue<(byte Type, byte[] Payload)> backlog, CancellationToken ct)
        {
            await stream.WriteAsync(RtdeProtocol.BuildRequestProtocolVersion(), ct);
            if (!RtdeProtocol.ParseAccepted(await AwaitPackageAsync(stream, framer, backlog, RtdeProtocol.RequestProtocolVersion, ct)))
                throw new NotSupportedException($"컨트롤러가 RTDE 프로토콜 v{RtdeProtocol.ProtocolVersion}를 거부 — URControl 3.10+/5.4+ 필요.");

            await stream.WriteAsync(RtdeProtocol.BuildSetupOutputs(RequestedFrequencyHz, OutputVariables), ct);
            (byte recipeId, string[] types) = RtdeProtocol.ParseSetupOutputsReply(
                await AwaitPackageAsync(stream, framer, backlog, RtdeProtocol.SetupOutputs, ct));

            if (types.Length != OutputVariables.Length)
                throw new InvalidDataException($"출력 레시피 응답 변수 수 불일치 (요청 {OutputVariables.Length}, 응답 {types.Length}).");
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == "NOT_FOUND")
                    throw new NotSupportedException($"컨트롤러가 출력 변수 '{OutputVariables[i]}'를 지원하지 않습니다 — 펌웨어 확인 필요.");
                if (types[i] != ExpectedTypes[i])
                    throw new InvalidDataException($"'{OutputVariables[i]}' 타입 불일치 (기대 {ExpectedTypes[i]}, 응답 {types[i]}).");
            }
            _recipeId = recipeId;

            await stream.WriteAsync(RtdeProtocol.BuildStart(), ct);
            if (!RtdeProtocol.ParseAccepted(await AwaitPackageAsync(stream, framer, backlog, RtdeProtocol.Start, ct)))
                throw new InvalidOperationException("컨트롤러가 RTDE 스트림 시작을 거부했습니다.");
        }

        /// <summary>지정 타입 패키지가 올 때까지 수신 — 다른 타입(TEXT_MESSAGE 등)은 건너뛴다.</summary>
        private static async Task<byte[]> AwaitPackageAsync(NetworkStream stream, RtdeFramer framer,
            Queue<(byte Type, byte[] Payload)> backlog, byte type, CancellationToken ct)
        {
            var buffer = new byte[4096];
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(HandshakeStepTimeoutMs);

            while (true)
            {
                while (backlog.Count > 0)
                {
                    (byte t, byte[] payload) = backlog.Dequeue();
                    if (t == type) return payload;
                }

                int n = await stream.ReadAsync(buffer, stepCts.Token);
                if (n == 0) throw new IOException("핸드셰이크 중 컨트롤러가 연결을 종료했습니다.");
                foreach ((byte Type, byte[] Payload) pkg in framer.Append(buffer.AsSpan(0, n)))
                    backlog.Enqueue(pkg);
            }
        }

        private async Task ReadLoopAsync(TcpClient tcp, RtdeFramer framer,
            Queue<(byte Type, byte[] Payload)> backlog, CancellationToken ct)
        {
            var buffer = new byte[8192];
            long packets = 0;

            try
            {
                NetworkStream stream = tcp.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    // 핸드셰이크 뒤에 이미 도착해 있던 데이터 패키지부터 소진.
                    while (backlog.Count > 0)
                    {
                        (byte t, byte[] payload) = backlog.Dequeue();
                        EmitIfDataPackage(t, payload, ref packets);
                    }

                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleCts.CancelAfter(IdleTimeoutMs);

                    int n;
                    try { n = await stream.ReadAsync(buffer, idleCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Notification?.Invoke(this, $"RTDE 스트림 무수신 {IdleTimeoutMs / 1000}s — 링크 사망 판정.");
                        SetState(TelemetrySourceState.Faulted);
                        return;
                    }

                    if (n == 0) // 원격 종료
                    {
                        if (!_userDisconnect)
                        {
                            Notification?.Invoke(this, "컨트롤러가 RTDE 연결을 종료했습니다.");
                            SetState(TelemetrySourceState.Faulted);
                        }
                        return;
                    }

                    foreach ((byte t, byte[] payload) in framer.Append(buffer.AsSpan(0, n)))
                        EmitIfDataPackage(t, payload, ref packets);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !_userDisconnect)
            {
                Notification?.Invoke(this, "스트림 오류: " + ex.GetBaseException().Message);
                SetState(TelemetrySourceState.Faulted);
            }
        }

        private void EmitIfDataPackage(byte type, byte[] payload, ref long packets)
        {
            if (type != RtdeProtocol.DataPackage) return; // TEXT_MESSAGE 등은 무시 (수신 전용 채널)

            (byte recipeId, double[] v) = RtdeProtocol.ParseDataPackage(payload);
            if (recipeId != _recipeId || v.Length != ValuesPerFrame)
            {
                if (packets == 0)
                    Notification?.Invoke(this, $"예상 밖 데이터 패키지 (레시피 {recipeId}, 값 {v.Length}개) — 건너뜀.");
                return;
            }

            if (packets++ == 0)
                Notification?.Invoke(this, $"첫 패키지 수신 — 축 6개, 컨트롤러 클럭 {v[0]:0.0}s.");
            FrameReceived?.Invoke(this, ToFrame(v));
        }

        private RobotTelemetryFrame ToFrame(double[] v) => new()
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ControllerClock = v[0],
            VendorId = Capabilities.VendorId,
            ChannelId = Capabilities.ChannelId,
            JointPositionDeg = Axes(v, 1, RadToDeg),   // actual_q: rad → °
            JointVelocityDegS = Axes(v, 7, RadToDeg),  // actual_qd: rad/s → °/s
            MotorCurrentA = Axes(v, 13, 1),            // actual_current: A
            ModelTorqueNm = Axes(v, 19, 1),            // target_moment: Nm (모델 목표 토크 — 실측 아님)
            TemperatureC = Axes(v, 25, 1),             // joint_temperatures: ℃
        };

        private static float[] Axes(double[] v, int at, double scale)
        {
            var r = new float[6];
            for (int i = 0; i < 6; i++) r[i] = (float)(v[at + i] * scale);
            return r;
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
