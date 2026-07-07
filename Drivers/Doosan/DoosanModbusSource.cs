using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Comms;

namespace BODA.CMS.Drivers.Doosan
{
    /// <summary>
    /// 두산 범용(Modbus TCP) 채널 드라이버 — Basic 등급.
    ///
    /// 실측 레지스터 맵 (CS-01, DRCF V2.11.1, unitId=1, 0-based 홀딩, FC03만):
    ///   270~275 = 축별 위치 J1~J6 (int16 × 0.1 = ° — 스케일 실측 확정)
    ///   300~305 = 축별 온도 (int16, ≈1℃/count 추정 — 미확정이라 VendorRaw)
    ///   400~405 = 축별 전류/토크 (int16 원시 — 스케일 미확정이라 VendorRaw)
    ///
    /// 연결은 주입받은 <see cref="ModbusConnectionService"/>(단일 TCP 세션)를 재사용하며,
    /// 이 드라이버는 세션을 닫지 않는다(연결 프로브 카드와 공유 — 소유자는 컴포지션 루트).
    /// </summary>
    public sealed class DoosanModbusSource : IRobotTelemetrySource
    {
        public const ushort PositionStart = 270;
        public const ushort TemperatureStart = 300;
        public const ushort CurrentStart = 400;
        public const float PositionScaleDeg = 0.1f;

        /// <summary>VendorRaw 키 — 온도 원시값(300~305, ≈1℃/count 추정). 키는 UI 라벨 열(8자)에 맞춘 짧은 이름.</summary>
        public const string RawTemperatureKey = "temp_raw";
        /// <summary>VendorRaw 키 — 전류/토크 원시값(400~405, 스케일 미확정).</summary>
        public const string RawCurrentTorqueKey = "cur_raw";

        public static readonly RobotCapabilities StaticCapabilities = new()
        {
            VendorId = "doosan",
            ChannelId = "modbus",
            DisplayName = "두산 Modbus (범용)",
            AxisCount = 6,
            NominalSampleRateHz = 10,      // 100ms 폴링
            DefaultPort = 502,
            HasJointTorqueSensor = false,
            HasMotorCurrent = false,       // 400~405 존재하나 스케일 미확정 → VendorRaw만 (규약: 정규화 완료 신호만 true)
            HasTemperature = false,        // 300~305 스케일 추정 단계 → VendorRaw만
            IsPassive = true,              // FC03 읽기 전용
        };

        private readonly ModbusConnectionService _modbus;
        private readonly byte _unitId;
        private readonly int _intervalMs;
        private readonly bool _ownsConnection;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <param name="ownsConnection">
        /// true면 이 드라이버가 Modbus 세션 수명을 소유한다(해제 시 세션 닫음) — headless 수집기용.
        /// false(기본)면 세션은 외부(연결 프로브 카드)와 공유되며 닫지 않는다 — WPF 앱용.
        /// </param>
        public DoosanModbusSource(ModbusConnectionService modbus, byte unitId = 1, int intervalMs = 100, bool ownsConnection = false)
        {
            _modbus = modbus;
            _unitId = unitId;
            _intervalMs = intervalMs;
            _ownsConnection = ownsConnection;
        }

        public RobotCapabilities Capabilities => StaticCapabilities;
        public TelemetrySourceState State { get; private set; } = TelemetrySourceState.Disconnected;

        public event EventHandler<RobotTelemetryFrame>? FrameReceived;
        public event EventHandler<TelemetrySourceState>? StateChanged;
        public event EventHandler<string>? Notification;

        public async Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default)
        {
            if (State == TelemetrySourceState.Connected) return;

            // 재연결 시맨틱: Faulted로 끝난 이전 폴링 루프의 잔재를 정리하고 새로 시작한다.
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;

            SetState(TelemetrySourceState.Connecting);

            try
            {
                if (!_modbus.IsConnected)
                    await _modbus.ConnectAsync(endpoint.Host, endpoint.Port ?? Capabilities.DefaultPort, ct: ct);
            }
            catch
            {
                SetState(TelemetrySourceState.Disconnected);
                throw;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
            SetState(TelemetrySourceState.Connected);
            Notification?.Invoke(this,
                $"Modbus 폴링 시작 (위치 {PositionStart}~·온도 {TemperatureStart}~·전류 {CurrentStart}~, unit={_unitId}, {1000.0 / _intervalMs:0}Hz).");
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
            if (_ownsConnection) _modbus.Disconnect(); // 재연결 시 스테일 소켓 없이 새 세션으로
            SetState(TelemetrySourceState.Disconnected);
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            const int axes = 6;
            const int maxConsecutiveErrors = 20; // ~2초(100ms 폴링) 연속 실패 → 링크 사망으로 판정
            int consecutiveErrors = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 공유 세션이 외부(연결 카드)에서 닫혔으면 폴링을 유지할 수 없다.
                    if (!_modbus.IsConnected)
                    {
                        Notification?.Invoke(this, "Modbus 세션이 닫혀 폴링을 중단합니다.");
                        SetState(TelemetrySourceState.Faulted);
                        return;
                    }

                    // 블록당 ≤6 레지스터라 FC03 한도(125) 내에서 3회 읽기.
                    ushort[] pos = await _modbus.TryReadHoldingAsync(_unitId, PositionStart, axes);
                    ushort[] temp = await _modbus.TryReadHoldingAsync(_unitId, TemperatureStart, axes);
                    ushort[] cur = await _modbus.TryReadHoldingAsync(_unitId, CurrentStart, axes);
                    consecutiveErrors = 0;

                    FrameReceived?.Invoke(this, ToFrame(DateTime.UtcNow, pos, temp, cur));
                }
                catch (Exception ex)
                {
                    Notification?.Invoke(this, "Modbus 읽기 오류: " + ex.GetBaseException().Message);

                    // TcpClient.Connected는 원격 절단 후에도 stale true일 수 있어
                    // 연속 오류 횟수로 링크 사망을 판정한다 (수집기 재연결 트리거).
                    if (++consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Notification?.Invoke(this, $"연속 읽기 오류 {consecutiveErrors}회 — 링크 사망 판정, 재연결 필요.");
                        SetState(TelemetrySourceState.Faulted);
                        return;
                    }
                }

                try { await Task.Delay(_intervalMs, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// 레지스터 원시값 → 공용 프레임. 위치만 스케일 확정(×0.1°)이라 정규화하고,
        /// 온도·전류/토크는 규약대로 VendorRaw에 원시 보존한다. (유닛테스트 대상 — 순수 함수)
        /// </summary>
        internal static RobotTelemetryFrame ToFrame(DateTime utcNow, ushort[] pos, ushort[] temp, ushort[] cur)
        {
            return new RobotTelemetryFrame
            {
                ReceivedAtUtc = utcNow,
                VendorId = StaticCapabilities.VendorId,
                ChannelId = StaticCapabilities.ChannelId,
                JointPositionDeg = ToScaled(pos, PositionScaleDeg),
                VendorRaw = new Dictionary<string, double[]>
                {
                    [RawTemperatureKey] = ToRaw(temp),
                    [RawCurrentTorqueKey] = ToRaw(cur),
                },
            };
        }

        private static float[] ToScaled(ushort[] regs, float scale)
        {
            var r = new float[regs.Length];
            for (int i = 0; i < regs.Length; i++) r[i] = (short)regs[i] * scale; // 부호 있는 int16
            return r;
        }

        private static double[] ToRaw(ushort[] regs)
        {
            var r = new double[regs.Length];
            for (int i = 0; i < regs.Length; i++) r[i] = (short)regs[i];
            return r;
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
