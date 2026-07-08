namespace BODA.CMS.Vision
{
    /// <summary>8비트 그레이스케일 이미지 — 카메라 프레임/합성 씬의 공용 컨테이너.</summary>
    public sealed class GrayImage
    {
        public GrayImage(int width, int height, byte[]? pixels = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (pixels is not null && pixels.Length != width * height)
                throw new ArgumentException("픽셀 버퍼 크기가 width×height와 다릅니다.", nameof(pixels));

            Width = width;
            Height = height;
            Pixels = pixels ?? new byte[width * height];
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>행 우선(row-major) 픽셀 버퍼.</summary>
        public byte[] Pixels { get; }

        public byte this[int x, int y]
        {
            get => Pixels[y * Width + x];
            set => Pixels[y * Width + x] = value;
        }
    }
}
