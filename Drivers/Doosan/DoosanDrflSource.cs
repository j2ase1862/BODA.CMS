using System;
using System.Threading;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan.Drfl;

namespace BODA.CMS.Drivers.Doosan
{
    /// <summary>
    /// 두산 네이티브(DRFL) 채널 드라이버 — Pro 등급.
    /// <see cref="DrflMonitorService"/>(패시브 콜백, 비개입)를 감싸 공용 프레임을 방출한다.
    /// DRFL 원본 단위가 이미 정규화 규약과 일치(° / °/s / A / Nm / ℃)하므로 환산 없이 매핑만 한다.
    /// </summary>
    public sealed class DoosanDrflSource : IRobotTelemetrySource
    {
        public static readonly RobotCapabilities StaticCapabilities = new()
        {
            VendorId = "doosan",
            ChannelId = "drfl",
            DisplayName = "두산 DRFL (네이티브)",
            AxisCount = 6,
            NominalSampleRateHz = 100,      // _SetOnMonitoringData 실측 ≈100Hz
            DefaultPort = 12345,
            HasJointTorqueSensor = true,    // H2017: 전축 JTS 탑재 (_fActualJTS)
            HasMotorCurrent = true,         // _fActualMC (A)
            HasTemperature = true,          // _fActualMT (℃)
            IsPassive = true,               // 명령 권한 미취득 — 콜백 수동 수신만
        };

        private readonly DrflMonitorService _drfl = new();
        // 연결/해제 직렬화 — 네이티브 핸들에 대한 동시 Connect/Disconnect 를 막는다
        // (중지 버튼 연타·제조사 전환과 재연결이 겹치는 경우).
        private readonly SemaphoreSlim _opGate = new(1, 1);
        private volatile bool _userDisconnect;

        public RobotCapabilities Capabilities => StaticCapabilities;
        public TelemetrySourceState State { get; private set; } = TelemetrySourceState.Disconnected;

        public event EventHandler<RobotTelemetryFrame>? FrameReceived;
        public event EventHandler<TelemetrySourceState>? StateChanged;
        public event EventHandler<string>? Notification;

        public DoosanDrflSource()
        {
            _drfl.SampleReceived += OnSample;
            _drfl.StateChanged += s => Notification?.Invoke(this, $"로봇 상태: {s}");
            _drfl.Disconnected += OnDrflDisconnected;
        }

        public async Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default)
        {
            if (State == TelemetrySourceState.Connected) return;

            await _opGate.WaitAsync(ct);
            try
            {
                // 재연결 시맨틱: Faulted(컨트롤러 측 끊김) 후에는 DrflMonitorService 내부 핸들과
                // _connected 플래그가 남아 있어 Connect()가 조용히 no-op 된다 — 먼저 정리한다.
                // 해제도 네이티브 스레드 정리를 기다리며 블로킹될 수 있어 백그라운드에서.
                await Task.Run(_drfl.Disconnect, CancellationToken.None);

                SetState(TelemetrySourceState.Connecting);
                _userDisconnect = false;

                try
                {
                    uint port = (uint)(endpoint.Port ?? Capabilities.DefaultPort);
                    // 네이티브 호출은 잠시 블로킹될 수 있으므로 백그라운드에서.
                    await Task.Run(() => _drfl.Connect(endpoint.Host, port), ct);
                }
                catch
                {
                    SetState(TelemetrySourceState.Disconnected);
                    throw;
                }

                SetState(TelemetrySourceState.Connected);
                Notification?.Invoke(this, "DRFL 연결 성공. 콜백 수신 시작(명령 권한 미취득 — 비개입).");

                try
                {
                    SYSTEM_VERSION v = _drfl.GetSystemVersion();
                    Notification?.Invoke(this, $"컨트롤러={v._szController}, 모델={v._szRobotModel}, S/N={v._szRobotSerial}");
                }
                catch (Exception ex)
                {
                    Notification?.Invoke(this, "시스템 버전 조회 생략: " + ex.GetBaseException().Message);
                }
            }
            finally { _opGate.Release(); }
        }

        public async Task DisconnectAsync()
        {
            _userDisconnect = true;
            await _opGate.WaitAsync();
            try
            {
                // ⚠️ 네이티브 CloseConnection/DestroyRobotControl 은 내부 수신 스레드 정리를
                // 기다리며 수 초 블로킹될 수 있다 — UI 스레드(중지 버튼)에서 직접 부르면
                // 저사양 PC에서 앱 전체가 멈춘 것처럼 보인다. 반드시 백그라운드에서.
                await Task.Run(_drfl.Disconnect);
                SetState(TelemetrySourceState.Disconnected);
            }
            finally { _opGate.Release(); }
        }

        // ⚠️ 네이티브(DRFL) 스레드에서 호출 — 구독자가 UI 마샬링 책임.
        private void OnSample(MonitoringSample s)
        {
            FrameReceived?.Invoke(this, new RobotTelemetryFrame
            {
                ReceivedAtUtc = DateTime.UtcNow,
                ControllerClock = s.SyncTime,
                VendorId = Capabilities.VendorId,
                ChannelId = Capabilities.ChannelId,
                JointPositionDeg = s.JointPosition,
                JointVelocityDegS = s.JointVelocity,
                MotorCurrentA = s.MotorCurrent,
                JointTorqueNm = s.JointTorqueSensor,
                ModelTorqueNm = s.DynamicTorque,
                ExternalTorqueNm = s.ExternalJointTorque,
                TemperatureC = s.MotorTemperature,
            });
        }

        private void OnDrflDisconnected()
        {
            if (_userDisconnect) return; // 정상 해제 경로는 DisconnectAsync에서 상태 처리
            Notification?.Invoke(this, "DRFL 연결이 끊겼습니다.");
            SetState(TelemetrySourceState.Faulted);
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
            _drfl.Dispose();
        }
    }
}
