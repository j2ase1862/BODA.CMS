using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BODA.CMS.Analytics;
using BODA.CMS.Analytics.Ml;
using BODA.CMS.Core.Telemetry;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>P3 ML 이상탐지 — 피처 정의·모니터 판정·ONNX 추론.</summary>
    public class MlAnomalyTests
    {
        private sealed class StubScorer : IAnomalyScorer
        {
            public double Value = 1.0;
            public double Score(float[] features) => Value;
        }

        private static readonly DateTime T0 = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);

        private static RobotTelemetryFrame TorqueFrame(int second, double torque) => new()
        {
            ReceivedAtUtc = T0.AddSeconds(second),
            VendorId = "test",
            ChannelId = "ch",
            JointPositionDeg = new float[6],
            JointTorqueNm = Enumerable.Repeat((float)torque, 6).ToArray(),
        };

        [Fact]
        public void 피처는_평균_표준편차_RMS_최소_최대_기울기다()
        {
            // 윈도 [0,1,2,3,4]: mean=2, std=1.5811(표본), rms=2.4495, min=0, max=4, slope=1
            float[] f = AnomalyFeatures.Compute(new double[] { 0, 1, 2, 3, 4 });

            Assert.Equal(6, f.Length);
            Assert.Equal(2f, f[0], precision: 4);
            Assert.Equal(1.5811f, f[1], precision: 3);
            Assert.Equal(2.44949f, f[2], precision: 4);
            Assert.Equal(0f, f[3]);
            Assert.Equal(4f, f[4]);
            Assert.Equal(1f, f[5], precision: 4);
        }

        [Fact]
        public void 임계_미달이_디바운스만큼_지속되면_ML_이상_알림()
        {
            var cbm = new CbmMonitor(new CbmOptions { LearningAggregates = 3, SpikeZ = 1000, DriftZ = 1000 });
            var stub = new StubScorer { Value = 1.0 };
            var info = new MlModelInfo { Window = 3, Threshold = 0.0 };
            var ml = new MlAnomalyMonitor(stub, info, new MlOptions { Debounce = 2, ResolveDebounce = 2 });
            ml.Attach(cbm);
            var alerts = new List<CbmAlert>();
            ml.AlertRaised += alerts.Add;

            int t = 0;
            // 기준선 학습(3집계) — 학습 중엔 z가 없어 ML도 무음.
            foreach (double v in new[] { 10.0, 10.1, 9.9, 10.0 }) cbm.Ingest(TorqueFrame(t++, v));
            // 정상 구간: 윈도(3)가 차고 스코어링 시작 — 점수 1.0 > 임계 0 → 무음.
            for (int i = 0; i < 5; i++) cbm.Ingest(TorqueFrame(t++, 10.0));
            Assert.Empty(alerts);
            Assert.True(ml.Snapshot.ScoredWindows > 0);

            // 이상 구간: 점수가 임계 밑으로 — 디바운스 2회 후 축당 1건.
            stub.Value = -0.5;
            for (int i = 0; i < 4; i++) cbm.Ingest(TorqueFrame(t++, 10.0));

            CbmAlert[] onset = alerts.Where(a => a.Kind == "ML 이상").ToArray();
            Assert.Equal(6, onset.Length); // 6축 각각 1건, 중복 없음
            Assert.All(onset, a => Assert.Equal(CbmSeverity.Warning, a.Severity));
            Assert.Equal(6, ml.Snapshot.ActiveAlertCount);

            // 정상 복귀 → 해제 통지.
            stub.Value = 1.0;
            for (int i = 0; i < 4; i++) cbm.Ingest(TorqueFrame(t++, 10.0));
            Assert.Contains(alerts, a => a.Kind == "ML 복귀" && a.Severity == CbmSeverity.Info);
            Assert.Equal(0, ml.Snapshot.ActiveAlertCount);
        }

        [Fact]
        public void ONNX_모델은_정상_윈도보다_비정상_윈도에_낮은_점수를_준다()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "models");
            string onnx = Path.Combine(dir, MlAnomalyMonitor.OnnxFileName);
            string sidecar = Path.Combine(dir, MlAnomalyMonitor.SidecarFileName);
            if (!File.Exists(onnx) || !File.Exists(sidecar)) return; // 모델 미생성 환경 — 소프트 스킵

            var info = MlModelInfo.Load(sidecar);
            using var scorer = new OnnxAnomalyScorer(onnx);

            // 정상: N(0,1) 범위의 잔잔한 z-윈도 / 비정상: 큰 계단(스파이크성) 윈도.
            double normal = scorer.Score(AnomalyFeatures.Compute(
                new[] { 0.2, -0.5, 0.1, 0.8, -0.3, 0.0, 0.4, -0.6, 0.2, -0.1 }));
            double anomalous = scorer.Score(AnomalyFeatures.Compute(
                new[] { 0.1, -0.2, 0.3, 0.0, 40.0, 42.0, 41.0, 43.0, 42.0, 41.0 }));

            Assert.True(normal > info.Threshold, $"정상 윈도가 임계 미달: {normal} <= {info.Threshold}");
            Assert.True(anomalous < info.Threshold, $"비정상 윈도가 임계 이상: {anomalous} >= {info.Threshold}");
            Assert.True(anomalous < normal);
        }
    }
}
