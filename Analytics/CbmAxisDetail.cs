namespace BODA.CMS.Analytics
{
    /// <summary>
    /// 신호·축 1개의 CBM 현재 상태 — 로봇 스켈레톤 뷰·히트맵 등 상세 시각화용.
    /// Z(즉시)·DriftZ(추세)는 마지막 집계 창 기준, 학습 전이면 둘 다 0.
    /// </summary>
    public sealed record CbmAxisDetail(
        string Signal, int Axis, double Z, double DriftZ, bool AlertActive, bool Learned)
    {
        /// <summary>시각화 색 판정용 대표 이탈도 — 즉시·추세 중 큰 쪽의 절대값.</summary>
        public double WorstZ => System.Math.Max(System.Math.Abs(Z), System.Math.Abs(DriftZ));
    }
}
