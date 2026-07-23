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

        /// <summary>초 단위로 전진하는 온도 프레임. 6축 모두 같은 값.</summary>
        private static RobotTelemetryFrame TempFrame(int second, double tempC) => new()
        {
            ReceivedAtUtc = T0.AddSeconds(second),
            VendorId = "test",
            ChannelId = "ch",
            JointPositionDeg = new float[6],
            TemperatureC = Enumerable.Repeat((float)tempC, 6).ToArray(),
        };

        /// <summary>온도 기준선 학습(30℃ 부근) + 다음 프레임 시각 반환.</summary>
        private static int LearnTemp(CbmMonitor m)
        {
            for (int i = 0; i <= 3; i++) m.Ingest(TempFrame(i, 30.0));
            return 4;
        }

        [Fact]
        public void 온도_웜업은_건강도에_영향이_없다()
        {
            // 실기 증상 재현: 콜드 스타트(30℃) 기준선 학습 후 수 시간 가동으로 55℃까지 상승.
            // 기준선 z 방식이면 σ 하한(0.3℃) 때문에 z≈83 → 건강도 0. 절대 임계 방식은 100 유지.
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = LearnTemp(m);
            foreach (double temp in new[] { 40.0, 50.0, 55.0, 59.0 }) m.Ingest(TempFrame(t++, temp));

            Assert.Equal(100, m.Snapshot.HealthScore);
            Assert.Empty(alerts);
        }

        [Fact]
        public void 온도는_경고임계부터_선형으로_감점된다()
        {
            (CbmMonitor m, _) = NewMonitor();
            int t = LearnTemp(m);
            // 67.5℃ = 경고(60)~알람(75)의 중간 → 의사 z = 3 → 건강도 100 - 25×(3-1) = 50.
            m.Ingest(TempFrame(t++, 67.5));
            m.Ingest(TempFrame(t++, 67.5)); // 버킷 마감 트리거

            Assert.Equal(50, m.Snapshot.HealthScore);
        }

        [Fact]
        public void 온도_순간_블립은_디바운스로_무시된다()
        {
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = LearnTemp(m);
            m.Ingest(TempFrame(t++, 76.0)); // 1버킷만 알람 임계 초과(블립)
            for (int i = 0; i < 4; i++) m.Ingest(TempFrame(t++, 45.0));

            Assert.Empty(alerts);
            Assert.Equal(100, m.Snapshot.HealthScore); // 복구 후 만점
        }

        [Fact]
        public void 온도_알람임계_지속시_과열_Alarm_후_복귀된다()
        {
            (CbmMonitor m, List<CbmAlert> alerts) = NewMonitor();
            int t = LearnTemp(m);

            // 80℃ 지속 — 디바운스(2) 충족 시 "과열" Alarm.
            for (int i = 0; i < 3; i++) m.Ingest(TempFrame(t++, 80.0));
            CbmAlert[] overheats = alerts.Where(a => a.Kind == "과열").ToArray();
            Assert.Equal(6, overheats.Length); // 축마다 1건, 중복 없음
            Assert.All(overheats, a => Assert.Equal(CbmSeverity.Alarm, a.Severity));
            Assert.Equal(0, m.Snapshot.HealthScore); // 알람 임계 초과 = 0점

            // 경고 임계 아래로 복귀 — 해제 통지.
            alerts.Clear();
            for (int i = 0; i < 4; i++) m.Ingest(TempFrame(t++, 45.0));
            Assert.Contains(alerts, a => a.Kind == "복귀" && a.Severity == CbmSeverity.Info);
            Assert.Equal(0, m.Snapshot.ActiveAlertCount);
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
