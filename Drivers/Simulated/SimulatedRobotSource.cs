using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Drivers.Simulated
{
    /// <summary>
    /// 가상 로봇 드라이버 — 하드웨어 없이 파이프라인·UI를 구동하기 위한 합성 텔레메트리.
    /// 계약 준수 시연을 겸한다: 코어·UI 수정 없이 카탈로그 등록만으로 새 "벤더"가 붙는다.
    ///
    /// 합성 모델: 축별 사인파 조그(주기 ~10초) + 자세 의존 모델 토크 + 노이즈.
    /// deep=false 프로필은 범용 채널을 모사(위치 정규화 + 온도/전류는 VendorRaw 원시값),
    /// deep=true 프로필은 네이티브 채널을 모사(토크센서·전류·온도 정규화 제공).
    /// </summary>
    public sealed class SimulatedRobotSource : IRobotTelemetrySource
    {
        private const int Axes = 6;
        private static readonly float[] HomeDeg = { 0f, -20f, 90f, 0f, 45f, 0f };
        private static readonly float[] AmplitudeDeg = { 60f, 25f, 30f, 40f, 20f, 90f };

        private readonly bool _deep;
        private readonly int _intervalMs;
        private readonly Random _rng = new(42);
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public SimulatedRobotSource(string channelId, string displayName, double rateHz, bool deep)
        {
            _deep = deep;
            _intervalMs = Math.Max(1, (int)Math.Round(1000.0 / rateHz));
            Capabilities = new RobotCapabilities
            {
                VendorId = "sim",
                ChannelId = channelId,
                DisplayName = displayName,
                AxisCount = Axes,
                NominalSampleRateHz = rateHz,
                DefaultPort = deep ? 5021 : 5020, // 가짜 포트 — 시뮬레이터는 실제 접속하지 않는다
                HasJointTorqueSensor = deep,
                HasMotorCurrent = deep,
                HasTemperature = deep,
                IsPassive = true,
            };
        }

        public RobotCapabilities Capabilities { get; }
        public TelemetrySourceState State { get; private set; } = TelemetrySourceState.Disconnected;

        public event EventHandler<RobotTelemetryFrame>? FrameReceived;
        public event EventHandler<TelemetrySourceState>? StateChanged;
        public event EventHandler<string>? Notification;

        public async Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default)
        {
            if (State == TelemetrySourceState.Connected) return;

            // 재연결 시맨틱: 이전 세션 잔재 정리 (계약 규약 — 실제 드라이버와 동일 동작).
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;

            SetState(TelemetrySourceState.Connecting);
            await Task.Delay(400, ct); // 접속 절차 흉내

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => GenerateLoopAsync(_cts.Token));
            SetState(TelemetrySourceState.Connected);
            Notification?.Invoke(this, $"가상 컨트롤러 연결(시뮬레이션) — {endpoint.Host} 는 무시됨, 합성 데이터 {Capabilities.NominalSampleRateHz:0}Hz 방출.");
        }

        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            if (_loop is not null)
            {
                try { await Task.WhenAny(_loop, Task.Delay(500)); } catch { /* 취소 예외 무시 */ }
            }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            SetState(TelemetrySourceState.Disconnected);
        }

        private async Task GenerateLoopAsync(CancellationToken ct)
        {
            DateTime start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                FrameReceived?.Invoke(this, BuildFrame((DateTime.UtcNow - start).TotalSeconds));
                try { await Task.Delay(_intervalMs, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private RobotTelemetryFrame BuildFrame(double t)
        {
            var pos = new float[Axes];
            var vel = new float[Axes];
            var model = new float[Axes];
            var jts = new float[Axes];
            var ext = new float[Axes];
            var cur = new float[Axes];
            var temp = new float[Axes];

            for (int j = 0; j < Axes; j++)
            {
                double w = 2 * Math.PI * 0.1;           // 사이클 10초
                double ph = j * 0.7;
                pos[j] = (float)(HomeDeg[j] + AmplitudeDeg[j] * Math.Sin(w * t + ph));
                vel[j] = (float)(AmplitudeDeg[j] * w * Math.Cos(w * t + ph));

                // 자세 의존 중력 부하 흉내 + 노이즈
                double gravity = 25.0 * Math.Cos(pos[j] * Math.PI / 180.0) * (1.0 - j * 0.12);
                double noise = (_rng.NextDouble() - 0.5) * 0.6;
                model[j] = (float)gravity;
                jts[j] = (float)(gravity + noise);
                ext[j] = (float)(jts[j] - model[j]);
                cur[j] = (float)(Math.Abs(gravity) / 9.0 + 0.4 + Math.Abs(noise) * 0.2);
                temp[j] = (float)(31.0 + j + 1.5 * Math.Sin(t / 45.0));
            }

            if (_deep)
            {
                return new RobotTelemetryFrame
                {
                    ReceivedAtUtc = DateTime.UtcNow,
                    ControllerClock = t,
                    VendorId = Capabilities.VendorId,
                    ChannelId = Capabilities.ChannelId,
                    JointPositionDeg = pos,
                    JointVelocityDegS = vel,
                    JointTorqueNm = jts,
                    ModelTorqueNm = model,
                    ExternalTorqueNm = ext,
                    MotorCurrentA = cur,
                    TemperatureC = temp,
                };
            }

            // 범용 채널 모사: 위치만 정규화, 온도·전류는 원시 카운트로(두산 Modbus 규약과 동일 형태).
            var rawTemp = new double[Axes];
            var rawCur = new double[Axes];
            for (int j = 0; j < Axes; j++)
            {
                rawTemp[j] = Math.Round(temp[j]);
                rawCur[j] = Math.Round(cur[j] * 100.0);
            }

            return new RobotTelemetryFrame
            {
                ReceivedAtUtc = DateTime.UtcNow,
                VendorId = Capabilities.VendorId,
                ChannelId = Capabilities.ChannelId,
                JointPositionDeg = pos,
                VendorRaw = new Dictionary<string, double[]>
                {
                    ["temp_raw"] = rawTemp,
                    ["cur_raw"] = rawCur,
                },
            };
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
