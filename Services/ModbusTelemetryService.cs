using System;
using System.Threading;
using System.Threading.Tasks;
using DoosanMonitor.Models;

namespace DoosanMonitor.Services
{
    /// <summary>
    /// Modbus(FC03) 주기 폴링으로 축별 위치·온도·전류/토크를 수집하는 서비스 (Phase 1 / Basic 등급).
    ///
    /// 실측 레지스터 맵 (CS-01, DRCF V2.11.1, unitId=1, 0-based 홀딩):
    ///   270~275 = 축별 위치 J1~J6 (int16 × 0.1 = °)
    ///   300~305 = 축별 온도 (int16, ℃ 추정)
    ///   400~405 = 축별 전류/토크 (int16 원시)
    ///
    /// 연결은 주입받은 <see cref="ModbusConnectionService"/>(연결 유지)를 재사용한다 — 단일 TCP 세션.
    /// </summary>
    public sealed class ModbusTelemetryService : IDisposable
    {
        public const ushort PositionStart = 270;
        public const ushort TemperatureStart = 300;
        public const ushort CurrentStart = 400;
        public const int AxisCount = 6;

        private readonly ModbusConnectionService _modbus;
        private readonly byte _unitId;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public ModbusTelemetryService(ModbusConnectionService modbus, byte unitId = 1)
        {
            _modbus = modbus;
            _unitId = unitId;
        }

        public bool IsPolling => _loop is { IsCompleted: false };

        /// <summary>폴링 1프레임 수신. ⚠️ 폴링 스레드에서 호출 — 구독자가 UI 마샬링 책임.</summary>
        public event Action<ModbusTelemetrySample>? SampleReceived;
        /// <summary>폴링 중 읽기 오류(루프는 계속됨).</summary>
        public event Action<string>? PollError;

        /// <summary>폴링 시작. 호출 전에 <paramref name="_modbus"/>가 연결돼 있어야 한다.</summary>
        public void Start(int intervalMs = 100)
        {
            if (IsPolling) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(intervalMs, _cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _loop?.Wait(1000); } catch { /* 취소 예외 무시 */ }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }

        private async Task PollLoopAsync(int intervalMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 블록당 ≤6 레지스터라 FC03 한도(125) 내에서 3회 읽기.
                    ushort[] pos = await _modbus.TryReadHoldingAsync(_unitId, PositionStart, AxisCount);
                    ushort[] temp = await _modbus.TryReadHoldingAsync(_unitId, TemperatureStart, AxisCount);
                    ushort[] cur = await _modbus.TryReadHoldingAsync(_unitId, CurrentStart, AxisCount);

                    var sample = new ModbusTelemetrySample
                    {
                        ReceivedAt = DateTime.Now,
                        JointPositionDeg = ToFloat(pos, 0.1f),
                        Temperature = ToShort(temp),
                        CurrentTorqueRaw = ToShort(cur),
                    };
                    SampleReceived?.Invoke(sample);
                }
                catch (Exception ex)
                {
                    PollError?.Invoke(ex.GetBaseException().Message);
                }

                try { await Task.Delay(intervalMs, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private static float[] ToFloat(ushort[] regs, float scale)
        {
            var r = new float[regs.Length];
            for (int i = 0; i < regs.Length; i++) r[i] = (short)regs[i] * scale; // 부호 있는 int16
            return r;
        }

        private static short[] ToShort(ushort[] regs)
        {
            var r = new short[regs.Length];
            for (int i = 0; i < regs.Length; i++) r[i] = (short)regs[i];
            return r;
        }

        public void Dispose() => Stop();
    }
}
