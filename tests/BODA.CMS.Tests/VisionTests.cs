using System;
using System.Collections.Generic;
using System.Linq;
using BODA.CMS.Analytics;
using BODA.CMS.Vision;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>P4 비전 진단 PoC — 서브픽셀 검출·드리프트/마모 판정.</summary>
    public class VisionTests
    {
        [Fact]
        public void 합성_마커를_서브픽셀_정확도로_검출한다()
        {
            var scene = new SyntheticMarkerScene(seed: 3) { NoiseAmplitude = 3.0 };
            GrayImage img = scene.Render(160, 120, cx: 80.3, cy: 60.7, radius: 12);

            MarkerObservation? obs = MarkerDetector.Detect(img);

            Assert.NotNull(obs);
            Assert.True(Math.Abs(obs!.CxPx - 80.3) < 0.15, $"cx 오차: {obs.CxPx - 80.3:0.000}px");
            Assert.True(Math.Abs(obs.CyPx - 60.7) < 0.15, $"cy 오차: {obs.CyPx - 60.7:0.000}px");
            Assert.True(Math.Abs(obs.DiameterPx - 24) < 1.5, $"지름: {obs.DiameterPx:0.0}px");
        }

        [Fact]
        public void 마커가_없으면_null을_반환한다()
        {
            // 노이즈만 있는 균일 배경 — 유효 블롭 없음.
            var img = new GrayImage(80, 60);
            var rng = new Random(1);
            for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = (byte)(220 + rng.Next(-5, 6));

            Assert.Null(MarkerDetector.Detect(img));
        }

        private static readonly VisionOptions Fast = new()
        {
            BaselineCycles = 10,
            DriftZ = 4,
            DriftDebounce = 2,
            WearZ = 4,
            WearDebounce = 2,
            MmPerPixel = 0.05,
        };

        private static MarkerObservation Obs(double cx, double cy = 100, double d = 24) =>
            new(cx, cy, d, (int)(Math.PI * d * d / 4));

        private static (RepeatabilityMonitor M, List<CbmAlert> Alerts) NewMonitor()
        {
            var m = new RepeatabilityMonitor("test", Fast);
            var alerts = new List<CbmAlert>();
            m.AlertRaised += alerts.Add;
            // 기준선: cx=100±0.1 (σ≈0.1px)
            for (int i = 0; i < 10; i++) m.Ingest(Obs(100 + (i % 2 == 0 ? 0.1 : -0.1)));
            return (m, alerts);
        }

        [Fact]
        public void 위치_드리프트가_지속되면_Alarm()
        {
            (RepeatabilityMonitor m, List<CbmAlert> alerts) = NewMonitor();
            Assert.True(m.IsLearned);

            m.Ingest(Obs(102)); // z ≈ 19σ
            m.Ingest(Obs(102));

            CbmAlert a = Assert.Single(alerts);
            Assert.Equal("비전 드리프트", a.Kind);
            Assert.Equal(CbmSeverity.Alarm, a.Severity);
            Assert.Contains("반복정밀도", a.Message);
            Assert.Equal(1, m.Snapshot.ActiveAlertCount);
        }

        [Fact]
        public void 지름_감소가_지속되면_마모_의심_Warning()
        {
            (RepeatabilityMonitor m, List<CbmAlert> alerts) = NewMonitor();

            m.Ingest(Obs(100, d: 20)); // 지름 24 → 20 (감소 방향 z 큼)
            m.Ingest(Obs(100, d: 20));

            CbmAlert a = Assert.Single(alerts);
            Assert.Equal("마모 의심", a.Kind);
            Assert.Equal(CbmSeverity.Warning, a.Severity);
        }

        [Fact]
        public void 지름_증가는_마모로_보지_않는다()
        {
            (RepeatabilityMonitor m, List<CbmAlert> alerts) = NewMonitor();

            m.Ingest(Obs(100, d: 28));
            m.Ingest(Obs(100, d: 28));

            Assert.Empty(alerts); // 단방향(감소) 판정
        }

        [Fact]
        public void 엔드투엔드_합성씬에서_드리프트를_잡는다()
        {
            var scene = new SyntheticMarkerScene(seed: 9) { NoiseAmplitude = 3.0 };
            var m = new RepeatabilityMonitor("e2e", Fast);
            var alerts = new List<CbmAlert>();
            m.AlertRaised += alerts.Add;
            var jitter = new Random(5);

            for (int c = 0; c < 40; c++)
            {
                double drift = c >= 20 ? (c - 20) * 0.2 : 0; // 학습 10 + 정상 10 + 드리프트
                double cx = 80.0 + drift + (jitter.NextDouble() - 0.5) * 0.2;
                GrayImage img = scene.Render(160, 120, cx, 60.0, 12);
                MarkerObservation? obs = MarkerDetector.Detect(img);
                Assert.NotNull(obs);
                m.Ingest(obs!);
            }

            Assert.Contains(alerts, a => a.Kind == "비전 드리프트");
        }
    }
}
