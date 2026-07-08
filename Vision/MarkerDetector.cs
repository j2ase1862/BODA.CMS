namespace BODA.CMS.Vision
{
    /// <summary>마커 관측 1건 — 픽셀 좌표계(서브픽셀).</summary>
    /// <param name="CxPx">중심 x (px).</param>
    /// <param name="CyPx">중심 y (px).</param>
    /// <param name="DiameterPx">등가원 지름 (px) — 면적 기반 2√(A/π).</param>
    /// <param name="AreaPx">블롭 면적 (px²).</param>
    public sealed record MarkerObservation(double CxPx, double CyPx, double DiameterPx, int AreaPx);

    /// <summary>
    /// 기준 마커(밝은 배경 위 어두운 원형 도트) 서브픽셀 검출기.
    /// Otsu 이진화 → 최대 어두운 연결 성분 → 어두움 가중 무게중심(서브픽셀).
    /// PoC 범위: 마커 1개(측정 스테이션당 1도트) 전제 — 다중 마커/ArUco는 실 카메라 도입 시.
    /// </summary>
    public static class MarkerDetector
    {
        /// <summary>유효 마커 최소 면적(px²) — 노이즈 블롭 배제.</summary>
        public const int MinArea = 20;

        /// <summary>마커/배경 클래스 평균 밝기 차 최소값 — 마커 없는(단봉) 장면에서 Otsu가
        /// 노이즈를 이분해 만드는 가짜 블롭을 배제한다.</summary>
        public const int MinContrast = 40;

        /// <summary>블롭 면적 상한(이미지 대비 비율) — 마커는 작은 도트라는 전제.</summary>
        public const double MaxAreaFraction = 0.25;

        public static MarkerObservation? Detect(GrayImage image)
        {
            byte threshold = OtsuThreshold(image.Pixels);
            if (!HasSufficientContrast(image.Pixels, threshold)) return null;

            (int area, int[] component) = LargestDarkComponent(image, threshold);
            if (area < MinArea || area > image.Pixels.Length * MaxAreaFraction) return null;

            // 어두움(threshold - v) 가중 무게중심 — 경계 안티앨리어싱까지 활용한 서브픽셀 추정.
            double wSum = 0, xSum = 0, ySum = 0;
            foreach (int idx in component)
            {
                if (idx < 0) break;
                int x = idx % image.Width;
                int y = idx / image.Width;
                double w = Math.Max(1, threshold - image.Pixels[idx]);
                wSum += w;
                xSum += w * x;
                ySum += w * y;
            }

            double diameter = 2.0 * Math.Sqrt(area / Math.PI);
            return new MarkerObservation(xSum / wSum, ySum / wSum, diameter, area);
        }

        private static bool HasSufficientContrast(byte[] pixels, byte threshold)
        {
            long darkSum = 0, darkN = 0, lightSum = 0, lightN = 0;
            foreach (byte p in pixels)
            {
                if (p < threshold) { darkSum += p; darkN++; }
                else { lightSum += p; lightN++; }
            }
            if (darkN == 0 || lightN == 0) return false;
            return (double)lightSum / lightN - (double)darkSum / darkN >= MinContrast;
        }

        private static byte OtsuThreshold(byte[] pixels)
        {
            Span<int> hist = stackalloc int[256];
            foreach (byte p in pixels) hist[p]++;

            long total = pixels.Length;
            long sumAll = 0;
            for (int i = 0; i < 256; i++) sumAll += (long)i * hist[i];

            long sumB = 0, wB = 0;
            double maxVar = -1;
            byte best = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;

                sumB += (long)t * hist[t];
                double mB = (double)sumB / wB;
                double mF = (double)(sumAll - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar)
                {
                    maxVar = between;
                    best = (byte)t;
                }
            }
            return best;
        }

        /// <summary>임계 미만(어두운) 픽셀의 최대 4-연결 성분. 반환 배열은 -1 패딩.</summary>
        private static (int Area, int[] Component) LargestDarkComponent(GrayImage img, byte threshold)
        {
            int w = img.Width, h = img.Height;
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            int bestArea = 0;
            int[] bestComponent = Array.Empty<int>();
            var current = new List<int>();

            for (int start = 0; start < w * h; start++)
            {
                if (visited[start] || img.Pixels[start] >= threshold) continue;

                current.Clear();
                stack.Push(start);
                visited[start] = true;
                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    current.Add(idx);
                    int x = idx % w, y = idx / w;

                    if (x > 0) TryPush(idx - 1);
                    if (x < w - 1) TryPush(idx + 1);
                    if (y > 0) TryPush(idx - w);
                    if (y < h - 1) TryPush(idx + w);
                }

                if (current.Count > bestArea)
                {
                    bestArea = current.Count;
                    bestComponent = current.ToArray();
                }
            }

            return (bestArea, bestComponent);

            void TryPush(int idx)
            {
                if (!visited[idx] && img.Pixels[idx] < threshold)
                {
                    visited[idx] = true;
                    stack.Push(idx);
                }
            }
        }
    }
}
