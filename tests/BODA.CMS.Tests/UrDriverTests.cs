using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.UR;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>UR RTDE 드라이버 — 프로토콜 조립·해석, 프레이머, 계약 준수(가짜 컨트롤러 TCP 서버) (§6 6단계).</summary>
    public class UrDriverTests
    {
        private const string RecipeTypes = "DOUBLE,VECTOR6D,VECTOR6D,VECTOR6D,VECTOR6D,VECTOR6D";

        // 라디안 원본 → 기대 각도: [0, 90, 180, -90, 45, -45]°
        private static readonly double[] SampleValues = BuildSampleValues();

        private static double[] BuildSampleValues()
        {
            var v = new double[31];
            v[0] = 0.008; // timestamp
            double[] qRad = { 0, Math.PI / 2, Math.PI, -Math.PI / 2, Math.PI / 4, -Math.PI / 4 };
            for (int i = 0; i < 6; i++)
            {
                v[1 + i] = qRad[i];              // actual_q (rad)
                v[7 + i] = Math.PI / 6;          // actual_qd (rad/s) → 30 °/s
                v[13 + i] = 0.5 * (i + 1);       // actual_current (A)
                v[19 + i] = i + 1;               // target_moment (Nm)
                v[25 + i] = 30 + i;              // joint_temperatures (℃)
            }
            return v;
        }

        [Fact]
        public void 프레이머는_분할_병합_패키지를_복원한다()
        {
            byte[] one = RtdeProtocol.BuildSetupOutputs(125, UrRtdeSource.OutputVariables);
            byte[] two = one.Concat(RtdeProtocol.BuildStart()).ToArray();

            var framer = new RtdeFramer();
            var found = new List<(byte Type, byte[] Payload)>();
            found.AddRange(framer.Append(two.AsSpan(0, 5)));   // 헤더 중간에서 절단
            found.AddRange(framer.Append(two.AsSpan(5, 40)));
            found.AddRange(framer.Append(two.AsSpan(45)));

            Assert.Equal(2, found.Count);
            Assert.Equal(RtdeProtocol.SetupOutputs, found[0].Type);
            Assert.Equal(RtdeProtocol.Start, found[1].Type);
            Assert.Equal(one.Length - 3, found[0].Payload.Length);
        }

        [Fact]
        public void 프로토콜_조립과_해석이_왕복한다()
        {
            // SETUP_OUTPUTS 요청: 주기(BE double) + 변수명
            byte[] setup = RtdeProtocol.BuildSetupOutputs(125, new[] { "timestamp", "actual_q" });
            Assert.Equal(125, BinaryPrimitives.ReadDoubleBigEndian(setup.AsSpan(3)));
            Assert.Equal("timestamp,actual_q", Encoding.UTF8.GetString(setup[11..]));

            // SETUP_OUTPUTS 응답: 레시피 id + 타입 목록
            byte[] reply = new byte[] { 7 }.Concat(Encoding.UTF8.GetBytes(RecipeTypes)).ToArray();
            (byte recipeId, string[] types) = RtdeProtocol.ParseSetupOutputsReply(reply);
            Assert.Equal(7, recipeId);
            Assert.Equal(UrRtdeSource.ExpectedTypes, types);

            // DATA_PACKAGE 왕복
            (byte dataRecipe, double[] values) = RtdeProtocol.ParseDataPackage(DataPayload(7, SampleValues));
            Assert.Equal(7, dataRecipe);
            Assert.Equal(SampleValues, values);
        }

        [Fact]
        public void 등급은_Pro다_전류_온도_정규화_선언()
        {
            // RTDE 문서가 단위 명세(A·℃) → Rokae 전례대로 capability true 선언 → Pro 자동 판정.
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Evaluate(UrRtdeSource.StaticCapabilities));
            Assert.True(UrRtdeSource.StaticCapabilities.IsPassive);
            Assert.False(UrRtdeSource.StaticCapabilities.HasJointTorqueSensor); // 관절 토크센서 미탑재
        }

        [Fact]
        public async Task 가짜_컨트롤러로_핸드셰이크와_정규화를_검증한다()
        {
            // §6 6단계: 버전 협상 → 레시피 구독 → 시작 → rad→° 정규화 프레임 → 원격 종료 시 Faulted.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task serverTask = Task.Run(() => RunFakeControllerAsync(listener, recipeId: 7, dataPackets: 5));

            await using var source = new UrRtdeSource();
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
                RobotTelemetryFrame f = frames[0];
                Assert.Equal("ur", f.VendorId);
                Assert.Equal("rtde", f.ChannelId);
                Assert.Equal(0.008, f.ControllerClock!.Value, precision: 6);
                Assert.Equal(90f, f.JointPositionDeg[1], precision: 3);   // π/2 rad → 90°
                Assert.Equal(-45f, f.JointPositionDeg[5], precision: 3);  // -π/4 rad → -45°
                Assert.Equal(30f, f.JointVelocityDegS![0], precision: 3); // π/6 rad/s → 30°/s
                Assert.Equal(0.5f, f.MotorCurrentA![0], precision: 3);
                Assert.Equal(3f, f.ModelTorqueNm![2], precision: 3);
                Assert.Equal(35f, f.TemperatureC![5], precision: 3);
                Assert.Null(f.JointTorqueNm); // 실측 토크 없음 — null 규약(0 채움 금지)
            }
            listener.Stop();
        }

        [Fact]
        public async Task 미지원_변수는_NOT_FOUND로_연결이_거부된다()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            string badTypes = "DOUBLE,NOT_FOUND,VECTOR6D,VECTOR6D,VECTOR6D,VECTOR6D";
            Task serverTask = Task.Run(() => RunFakeControllerAsync(listener, recipeId: 1, dataPackets: 0, badTypes));

            await using var source = new UrRtdeSource();
            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => source.ConnectAsync(new RobotEndpoint("127.0.0.1", port)));
            Assert.Contains("actual_q", ex.Message); // 어떤 변수가 문제인지 알려준다
            Assert.Equal(TelemetrySourceState.Disconnected, source.State);

            await serverTask;
            listener.Stop();
        }

        // ── 가짜 RTDE 컨트롤러: 핸드셰이크 응답 후 데이터 패키지를 (분할 송신으로) 흘리고 종료 ──
        private static async Task RunFakeControllerAsync(TcpListener listener, byte recipeId, int dataPackets,
            string types = RecipeTypes)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream s = client.GetStream();
            var framer = new RtdeFramer();
            var buf = new byte[4096];

            bool started = false;
            while (!started)
            {
                int n = await s.ReadAsync(buf);
                if (n == 0) return; // 드라이버가 먼저 끊음 (거부 시나리오)
                foreach ((byte type, byte[] _) in framer.Append(buf.AsSpan(0, n)))
                {
                    switch (type)
                    {
                        case RtdeProtocol.RequestProtocolVersion:
                            // 텍스트 통지를 끼워 넣어 핸드셰이크의 타입 스킵도 검증
                            await s.WriteAsync(Package(RtdeProtocol.TextMessage, Encoding.UTF8.GetBytes("fake")));
                            await s.WriteAsync(Package(RtdeProtocol.RequestProtocolVersion, new byte[] { 1 }));
                            break;
                        case RtdeProtocol.SetupOutputs:
                            byte[] payload = new[] { recipeId }.Concat(Encoding.UTF8.GetBytes(types)).ToArray();
                            await s.WriteAsync(Package(RtdeProtocol.SetupOutputs, payload));
                            if (types.Contains("NOT_FOUND")) return; // 드라이버가 예외로 끊는다
                            break;
                        case RtdeProtocol.Start:
                            await s.WriteAsync(Package(RtdeProtocol.Start, new byte[] { 1 }));
                            started = true;
                            break;
                    }
                }
            }

            byte[] data = Package(RtdeProtocol.DataPackage, DataPayload(recipeId, SampleValues));
            for (int i = 0; i < dataPackets; i++)
            {
                // TCP 분할을 흉내 내 두 조각으로 송신
                await s.WriteAsync(data.AsMemory(0, 60));
                await s.WriteAsync(data.AsMemory(60));
                await Task.Delay(20);
            }
        } // 송신 후 서버 종료 → 원격 close

        private static byte[] Package(byte type, byte[] payload)
        {
            var p = new byte[3 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)p.Length);
            p[2] = type;
            payload.CopyTo(p, 3);
            return p;
        }

        private static byte[] DataPayload(byte recipeId, double[] values)
        {
            var payload = new byte[1 + values.Length * 8];
            payload[0] = recipeId;
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(1 + i * 8), values[i]);
            return payload;
        }
    }
}
