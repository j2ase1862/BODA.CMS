namespace BODA.CMS.Analytics
{
    /// <summary>Welford 온라인 평균/분산 — 버퍼 없이 스트리밍으로 기준선을 학습한다.</summary>
    public sealed class RunningStats
    {
        private double _m2;

        public long Count { get; private set; }
        public double Mean { get; private set; }

        /// <summary>표본 분산(n-1). 표본 2개 미만이면 0.</summary>
        public double Variance => Count > 1 ? _m2 / (Count - 1) : 0;

        public double Std => Math.Sqrt(Variance);

        public void Add(double value)
        {
            Count++;
            double delta = value - Mean;
            Mean += delta / Count;
            _m2 += delta * (value - Mean);
        }
    }
}
