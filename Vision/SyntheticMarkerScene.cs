namespace BODA.CMS.Vision
{
    /// <summary>
    /// 합성 마커 씬 — 카메라 없이 비전 파이프라인을 검증하기 위한 렌더러
    /// (텔레메트리의 SimulatedRobotSource와 같은 역할).
    /// 밝은 배경 + 어두운 원형 도트, 경계 안티앨리어싱(커버리지) + 재현 가능한 노이즈.
    /// </summary>
    public sealed class SyntheticMarkerScene
    {
        private readonly Random _rng;

        public SyntheticMarkerScene(int seed = 42) => _rng = new Random(seed);

        public byte Background { get; init; } = 225;
        public byte MarkerLevel { get; init; } = 35;
        public double NoiseAmplitude { get; init; } = 4.0;

        /// <summary>중심(cx,cy)·반지름 r(px, 실수)의 마커 이미지를 렌더.</summary>
        public GrayImage Render(int width, int height, double cx, double cy, double radius)
        {
            var img = new GrayImage(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // 픽셀 중심 기준 거리 → 경계 1px 구간 선형 커버리지(안티앨리어싱).
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double coverage = Math.Clamp(radius + 0.5 - d, 0, 1);
                    double v = Background + (MarkerLevel - Background) * coverage;
                    v += (_rng.NextDouble() - 0.5) * 2 * NoiseAmplitude;
                    img[x, y] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
            return img;
        }
    }
}
