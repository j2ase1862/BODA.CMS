namespace BODA.CMS.Analytics.Ml
{
    /// <summary>
    /// z-정규화 집계값 윈도(기본 10초) → 고정 6차원 통계 피처.
    ///
    /// 입력이 이미 기준선 z-점수라 신호·벤더·샘플링 주기와 무관하게 같은 분포 공간에 놓인다 —
    /// 덕분에 단일 ONNX 모델이 전 신호·전 벤더를 커버한다(ROADMAP P3 리샘플링 규약).
    /// ⚠️ 이 피처 정의는 학습 스크립트(tools/ml/train_anomaly.py)와 정확히 일치해야 한다.
    /// </summary>
    public static class AnomalyFeatures
    {
        public static readonly string[] Names = { "mean", "std", "rms", "min", "max", "slope" };
        public static int Count => Names.Length;

        public static float[] Compute(IReadOnlyList<double> zWindow)
        {
            int n = zWindow.Count;
            if (n < 2) throw new ArgumentException("윈도 길이는 2 이상이어야 합니다.", nameof(zWindow));

            double sum = 0, sumSq = 0, min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = zWindow[i];
                sum += v;
                sumSq += v * v;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double mean = sum / n;
            double variance = Math.Max(0, (sumSq - n * mean * mean) / (n - 1)); // 표본 분산
            double rms = Math.Sqrt(sumSq / n);

            // 최소제곱 기울기 (x = 0..n-1, 집계 스텝당 변화량)
            double xMean = (n - 1) / 2.0;
            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                num += (i - xMean) * (zWindow[i] - mean);
                den += (i - xMean) * (i - xMean);
            }
            double slope = den > 0 ? num / den : 0;

            return new[] { (float)mean, (float)Math.Sqrt(variance), (float)rms, (float)min, (float)max, (float)slope };
        }
    }
}
