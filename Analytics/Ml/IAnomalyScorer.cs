namespace BODA.CMS.Analytics.Ml
{
    /// <summary>
    /// 피처 벡터 1개의 정상성 점수. IsolationForest decision_function 규약:
    /// <b>높을수록 정상</b>, 임계값(모델 사이드카) 미만이면 이상.
    /// </summary>
    public interface IAnomalyScorer
    {
        double Score(float[] features);
    }
}
