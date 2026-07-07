using System;
using System.Collections.Generic;
using System.Linq;
using BODA.CMS.Analytics;
using BODA.CMS.Core.Telemetry;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>CBM 엔진 — 기준선 학습·스파이크/드리프트 알림·해제 (P2).</summary>
    public class CbmMonitorTests
    {
        // 테스트용 축소 파라미터: 1초 집계, 3개 학습, 디바운스 2, 해제 2.
        private static readonly CbmOptions Fast = new()
        {
            LearningAggregates = 3,
            SpikeZ = 4,
            SpikeDebounce = 2,
            DriftZ = 3,
            DriftDebounce = 3,
            EwmaAlpha = 0.5,
            ResolveZ = 1.5,
            ResolveDebounce = 2,
        };

        private static readonly DateTime T0 = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>초 단위로 전진하는 토크 프레임(1프레임 = 1집계). 6축 모두 같은 값.</summary>
        private static RobotTelemetryFrame TorqueFrame(int second, double torque) => new()
        {
            ReceivedAtUtc = T0.AddSeconds(second),
            VendorId = "test",
            ChannelId = "ch",
            JointPositionDeg = new float[6],
            JointTorqueNm = Enumerable.Repeat((float)torque, 6).ToArray(),
        };

        private static (CbmMonitor Monitor, List<CbmAlert> Alerts) NewMonitor()
        {
            var monitor = new CbmMonitor(Fast);
            var alerts = new List<CbmAlert>();
            monitor.AlertRaised += alerts.Add;
            return (monitor, alerts);
        }

        /// <summary>기준선 학습용 시퀀스: 10, 10.1, 9.9 (μ=10, σ=0.1) + 다음 버킷 트리거.</summary>
        private static int Learn(CbmMonitor m)
        {
            m.Ingest(TorqueFrame(0, 10.0));
            m.Ingest(TorqueFrame(1, 10.1)); // → 0초 버킷 평가(학습 1)
            m.Ingest(TorqueFrame(2, 9.9));  // → 학습 2
            m.Ingest(TorqueFrame(3, 10.0)); // → 학습 3 (완료)
            return 4; // 다음 프레임 시각
        }

        [Fact]
        public void 러닝스탯은_평균과_표본표준편차를_계산한다()
        {
            var s = new RunningStats();
            foreach (double v in new[] { 10.0, 10.1, 9.9 }) s.Add(v);

            Assert.Equal(10.0, s.Mean, precision: 10);
            Assert.Equal(0.1, s.Std, precision: 10);
        }

        [Fact]
        public void 학습이_끝나면_감시_단계로_전환된다()
        {
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = Learn(m);
            m.Ingest(TorqueFrame(t, 10.0)); // 학습 완료 후 첫 정상 평가

            CbmSnapshot snap = m.Snapshot;
            Assert.Equal(CbmPhase.Monitoring, snap.Phase);
            Assert.Equal(100, snap.HealthScore);
            Assert.Empty(alerts); // 학습·정상 구간에서는 무음
        }

        [Fact]
        public void 급변은_디바운스_후_한_번만_Alarm을_낸다()
        {
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = Learn(m);

            // z = (20-10)/0.1 = 100 ≥ SpikeZ. 디바운스 2회 충족 시점에 1회 발화.
            m.Ingest(TorqueFrame(t++, 20.0));
            m.Ingest(TorqueFrame(t++, 20.0));
            m.Ingest(TorqueFrame(t++, 20.0)); // 버킷 마감 트리거

            CbmAlert[] spikes = alerts.Where(a => a.Kind == "급변").ToArray();
            Assert.Equal(6, spikes.Length); // 6축 동일 신호 → 축마다 1건, 중복 발화 없음
            Assert.All(spikes, a => Assert.Equal(CbmSeverity.Alarm, a.Severity));
            Assert.All(spikes, a => Assert.Equal("토크Nm", a.Signal));

            Assert.True(m.Snapshot.HealthScore <= 5); // z가 크므로 건강도 바닥
            Assert.Equal(6, m.Snapshot.ActiveAlertCount);
        }

        [Fact]
        public void 정상_복귀하면_해제_통지가_나온다()
        {
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = Learn(m);

            // 알람 유발
            m.Ingest(TorqueFrame(t++, 20.0));
            m.Ingest(TorqueFrame(t++, 20.0));
            m.Ingest(TorqueFrame(t++, 20.0));
            alerts.Clear();

            // 정상 복귀 (EWMA가 내려올 시간 포함 넉넉히)
            for (int i = 0; i < 10; i++) m.Ingest(TorqueFrame(t++, 10.0));

            Assert.Contains(alerts, a => a.Kind == "복귀" && a.Severity == CbmSeverity.Info);
            Assert.Equal(0, m.Snapshot.ActiveAlertCount);
        }

        [Fact]
        public void 완만한_드리프트는_Warning으로_잡는다()
        {
            var options = Fast with { SpikeZ = 1000 }; // 스파이크 경로 차단 — 드리프트만 검증
            var m = new CbmMonitor(options);
            var alerts = new List<CbmAlert>();
            m.AlertRaised += alerts.Add;

            int t = Learn(m);
            // EWMA(α=0.5)가 10.6으로 수렴 → driftZ = 0.6/0.1 = 6 ≥ 3, 디바운스 3회.
            for (int i = 0; i < 8; i++) m.Ingest(TorqueFrame(t++, 10.6));

            CbmAlert[] drifts = alerts.Where(a => a.Kind == "드리프트").ToArray();
            Assert.NotEmpty(drifts);
            Assert.All(drifts, a => Assert.Equal(CbmSeverity.Warning, a.Severity));
        }

        [Fact]
        public void 위치와_속도는_감시하지_않는다()
        {
            (CbmMonitor m, _) = NewMonitor();
            var frame = new RobotTelemetryFrame
            {
                ReceivedAtUtc = T0,
                VendorId = "test",
                ChannelId = "ch",
                JointPositionDeg = new float[] { 1, 2, 3, 4, 5, 6 },
                JointVelocityDegS = new float[6],
            };
            m.Ingest(frame);
            m.Ingest(new RobotTelemetryFrame
            {
                ReceivedAtUtc = T0.AddSeconds(1),
                VendorId = "test",
                ChannelId = "ch",
                JointPositionDeg = new float[6],
            });

            CbmSnapshot snap = m.Snapshot;
            Assert.Equal(0, snap.MonitoredCount);
            Assert.Equal(CbmPhase.Monitoring, snap.Phase); // 감시할 신호가 없으면 "감시 신호 없음"으로 정착
            Assert.Equal("감시 신호 없음", snap.WorstDescription);
        }

        [Fact]
        public void VendorRaw_신호도_감시_대상이다()
        {
            (CbmMonitor m, _) = NewMonitor();
            var frame = new RobotTelemetryFrame
            {
                ReceivedAtUtc = T0,
                VendorId = "doosan",
                ChannelId = "modbus",
                JointPositionDeg = new float[6],
                VendorRaw = new Dictionary<string, double[]> { ["cur_raw"] = new double[6] },
            };
            m.Ingest(frame);
            m.Ingest(TorqueFrame(1, 10)); // 버킷 마감

            Assert.True(m.Snapshot.MonitoredCount >= 6); // cur_raw 6축이 상태로 등록됨
        }
    }
}
