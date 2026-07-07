using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;

namespace BODA.CMS.Comms
{
    /// <summary>
    /// Modbus TCP 연결을 관리하는 서비스.
    /// 연결을 유지(_tcp/_master 보관)하므로, 이후 모니터링 단계에서 그대로 재사용할 수 있습니다.
    /// </summary>
    public sealed class ModbusConnectionService : IDisposable
    {
        private TcpClient? _tcp;
        private IModbusMaster? _master;

        public bool IsConnected => _tcp?.Connected ?? false;
        public IModbusMaster? Master => _master;

        public async Task ConnectAsync(string ip, int port, int timeoutMs = 3000, CancellationToken ct = default)
        {
            Disconnect();

            var tcp = new TcpClient();
            try
            {
                var connectTask = tcp.ConnectAsync(ip, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, ct));
                if (completed != connectTask)
                {
                    tcp.Dispose();
                    throw new TimeoutException($"연결 타임아웃 ({timeoutMs} ms)");
                }
                await connectTask; // 연결 예외(거부 등) 관찰

                var factory = new ModbusFactory();
                var master = factory.CreateMaster(tcp);
                master.Transport.ReadTimeout = 1000;
                master.Transport.WriteTimeout = 1000;

                _tcp = tcp;
                _master = master;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>홀딩 레지스터 시험 읽기. 실패해도 TCP 연결 자체는 유효할 수 있습니다.</summary>
        public Task<ushort[]> TryReadHoldingAsync(byte unitId, ushort start, ushort count)
        {
            if (_master is null) throw new InvalidOperationException("연결되지 않았습니다.");
            return Task.Run(() => _master.ReadHoldingRegisters(unitId, start, count));
        }

        public void Disconnect()
        {
            (_master as IDisposable)?.Dispose();
            _master = null;
            _tcp?.Close();
            _tcp?.Dispose();
            _tcp = null;
        }

        public void Dispose() => Disconnect();
    }
}
