using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Jaka;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>JAKA 드라이버 — 프레이머·파서·계약 준수(가짜 컨트롤러 TCP 서버) (§6 6단계).</summary>
    public class JakaDriverTests
    {
        private const string SamplePacket =
            """{"joint_actual_position":[10.5,-20.1,90.0,0.3,45.2,-5.7],"instCurrent":[0.4,1.2,0.9,0.3,0.2,0.1],"joint_temp":[31,32,33,34,35,36],"enabled":true,"task_state":3}""";

        [Fact]
        public void 프레이머는_쪼개지고_붙은_패킷을_복원한다()
        {
            var framer = new JakaStreamFramer();
            string two = SamplePacket + SamplePacket;

            // 임의 지점 분할 + 앞쪽 쓰레기 바이트
            var found = new List<string>();
            found.AddRange(framer.Append("GARBAGE" + two[..37]));
            found.AddRange(framer.Append(two[37..205]));
            found.AddRange(framer.Append(two[205..]));

            Assert.Equal(2, found.Count);
            Assert.All(found, f => Assert.Equal(SamplePacket, f));
        }

        [Fact]
        public void 프레이머는_문자열_안의_중괄호에_속지_않는다()
        {
            var framer = new JakaStreamFramer();
            const string tricky = """{"name":"중괄호 } 포함 \" 문자열","joint_actual_position":[1,2,3,4,5,6]}""";

            IReadOnlyList<string> found = framer.Append(tricky);

            Assert.Single(found);
            Assert.Equal(tricky, found[0]);
        }

        [Fact]
        public void 파서는_위치를_정규화하고_미확정_신호를_VendorRaw로_보존한다()
        {
            RobotTelemetryFrame? f = JakaPacketParser.Parse(SamplePacket, DateTime.UtcNow);

            Assert.NotNull(f);
            Assert.Equal("jaka", f!.VendorId);
            Assert.Equal("monitor", f.ChannelId);
            Assert.Equal(10.5f, f.JointPositionDeg[0], precision: 3);
            Assert.Equal(6, f.AxisCount);

            // 전류/온도는 실기 검증 전 — 정규화 필드는 비우고 원시로 보존 (§2 규약)
            Assert.Null(f.MotorCurrentA);
            Assert.Null(f.TemperatureC);
            Assert.Equal(new double[] { 0.4, 1.2, 0.9, 0.3, 0.2, 0.1 }, f.VendorRaw!["cur_raw"]);
            Assert.Equal(new double[] { 31, 32, 33, 34, 35, 36 }, f.VendorRaw["temp_raw"]);
        }

        [Fact]
        public void 파서는_data_래핑과_손상_패킷을_처리한다()
        {
            RobotTelemetryFrame? wrapped = JakaPacketParser.Parse(
                """{"len":123,"data":{"joint_actual_position":[1,2,3,4,5,6]}}""", DateTime.UtcNow);
            Assert.NotNull(wrapped);
            Assert.Equal(2f, wrapped!.JointPositionDeg[1]);

            Assert.Null(JakaPacketParser.Parse("{corrupt", DateTime.UtcNow));
            Assert.Null(JakaPacketParser.Parse("""{"no_position":true}""", DateTime.UtcNow));
        }

        [Fact]
        public void 등급은_capability_규약대로_Basic이다()
        {
            // 전류/토크가 VendorRaw 단계(Has*=false) → Basic. 실기 확정 후 상향 검토(§5.2).
            Assert.Equal(ProductTier.Basic, ProductTierEvaluator.Evaluate(JakaJsonSource.StaticCapabilities));
            Assert.True(JakaJsonSource.StaticCapabilities.IsPassive);
        }

        [Fact]
        public async Task 가짜_컨트롤러_스트림으로_계약_동작을_검증한다()
        {
            // §6 6단계: 접속 → 프레임 수신 → 원격 종료 시 Faulted (재연결 트리거) — 하드웨어 없이 검증.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream s = client.GetStream();
                byte[] packet = Encoding.UTF8.GetBytes(SamplePacket);
                for (int i = 0; i < 5; i++)
                {
                    // TCP 분할을 흉내 내 두 조각으로 송신
                    await s.WriteAsync(packet.AsMemory(0, 50));
                    await s.WriteAsync(packet.AsMemory(50));
                    await Task.Delay(30);
                }
            }); // 송신 후 서버 종료 → 원격 close

            await using var source = new JakaJsonSource();
            var frames = new List<RobotTelemetryFrame>();
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.FrameReceived += (_, f) => { lock (frames) frames.Add(f); };
            source.StateChanged += (_, st) => { if (st == TelemetrySourceState.Faulted) faulted.TrySetResult(); };

            await source.ConnectAsync(new RobotEndpoint("127.0.0.1", port));
            Assert.Equal(TelemetrySourceState.Connected, source.State);

            await serverTask;
            Assert.True(await Task.WhenAny(faulted.Task, Task.Delay(5000)) == faulted.Task,
                "원격 종료 후 Faulted 전이가 없음");

            lock (frames)
            {
                Assert.Equal(5, frames.Count);
                Assert.All(frames, f => Assert.Equal(6, f.AxisCount));
            }
            listener.Stop();
        }
    }
}
