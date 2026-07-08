using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Rokae;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>Rokae 드라이버 — 폴링 소스 계약 검증 (가짜 상태 클라이언트, §6 6단계).
    /// SDK 클라이언트(XCoreSdkStateClient)는 SDK 바이너리 확보 후 — 소스 로직은 여기서 전부 검증.</summary>
    public class RokaeDriverTests
    {
        private sealed class FakeStateClient : IRokaeStateClient
        {
            public int Queries;
            public bool FailQueries;
            public bool Connected;

            public Task ConnectAsync(string host, CancellationToken ct)
            {
                Connected = true;
                return Task.CompletedTask;
            }

            public Task<RokaeRobotState> QueryStateAsync(CancellationToken ct)
            {
                if (FailQueries) throw new TimeoutException("조회 타임아웃(모의)");
                Queries++;
                return Task.FromResult(new RokaeRobotState(
                    JointPositionDeg: new float[] { 1, 2, 3, 4, 5, 6 },
                    JointTorqueNm: new float[] { 10, 20, 30, 40, 50, 60 },
                    MotorCurrentA: new float[] { 0.5f, 1, 1.5f, 2, 2.5f, 3 },
                    TemperatureC: new float[] { 30, 31, 32, 33, 34, 35 }));
            }

            public Task DisconnectAsync()
            {
                Connected = false;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        [Fact]
        public void 등급은_Pro다_전축_토크센서_쇼케이스()
        {
            Assert.Equal(ProductTier.Pro, ProductTierEvaluator.Evaluate(RokaeXCoreSource.StaticCapabilities));
            Assert.True(RokaeXCoreSource.StaticCapabilities.IsPassive);
            Assert.True(RokaeXCoreSource.StaticCapabilities.HasJointTorqueSensor);
        }

        [Fact]
        public async Task 폴링으로_정규화_프레임을_방출한다()
        {
            var client = new FakeStateClient();
            await using var source = new RokaeXCoreSource(client, intervalMs: 10);
            var frames = new List<RobotTelemetryFrame>();
            source.FrameReceived += (_, f) => { lock (frames) frames.Add(f); };

            await source.ConnectAsync(new RobotEndpoint("192.168.0.160"));
            Assert.Equal(TelemetrySourceState.Connected, source.State);
            Assert.True(client.Connected);

            await WaitUntilAsync(() => { lock (frames) return frames.Count >= 3; });

            lock (frames)
            {
                RobotTelemetryFrame f = frames[0];
                Assert.Equal("rokae", f.VendorId);
                Assert.Equal("xcore-sdk", f.ChannelId);
                Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, f.JointPositionDeg);
                Assert.Equal(30f, f.JointTorqueNm![2]);   // 전축 토크센서 실측 → 정규화 필드
                Assert.Equal(0.5f, f.MotorCurrentA![0]);
                Assert.Null(f.JointVelocityDegS);          // 미제공 신호는 null 유지
                Assert.Null(f.VendorRaw);                  // 전 신호 정규화 — 원시 보존 불필요
            }

            await source.DisconnectAsync();
            Assert.Equal(TelemetrySourceState.Disconnected, source.State);
            Assert.False(client.Connected);
        }

        [Fact]
        public async Task 연속_조회_실패면_Faulted로_전이한다()
        {
            var client = new FakeStateClient();
            await using var source = new RokaeXCoreSource(client, intervalMs: 5);
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.StateChanged += (_, st) => { if (st == TelemetrySourceState.Faulted) faulted.TrySetResult(); };

            await source.ConnectAsync(new RobotEndpoint("192.168.0.160"));
            client.FailQueries = true;

            Assert.True(await Task.WhenAny(faulted.Task, Task.Delay(5000)) == faulted.Task,
                "연속 실패 후 Faulted 전이가 없음");
        }

        [Fact]
        public async Task Faulted_후_재연결이_가능하다()
        {
            var client = new FakeStateClient();
            await using var source = new RokaeXCoreSource(client, intervalMs: 5);
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.StateChanged += (_, st) => { if (st == TelemetrySourceState.Faulted) faulted.TrySetResult(); };

            await source.ConnectAsync(new RobotEndpoint("192.168.0.160"));
            client.FailQueries = true;
            await Task.WhenAny(faulted.Task, Task.Delay(5000));

            // 수집기 재연결 경로 그대로: Disconnect → Connect (계약 규약)
            await source.DisconnectAsync();
            client.FailQueries = false;
            int before = client.Queries;

            var frames = new List<RobotTelemetryFrame>();
            source.FrameReceived += (_, f) => { lock (frames) frames.Add(f); };
            await source.ConnectAsync(new RobotEndpoint("192.168.0.160"));

            await WaitUntilAsync(() => { lock (frames) return frames.Count >= 2; });
            Assert.Equal(TelemetrySourceState.Connected, source.State);
            Assert.True(client.Queries > before);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "대기 타임아웃");
                await Task.Delay(10);
            }
        }
    }
}
