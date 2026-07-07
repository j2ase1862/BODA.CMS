namespace BODA.CMS.Collector
{
    /// <summary>재연결 지수 백오프: 1s → 2s → 4s → … → 30s 상한 (P1 재연결 규약).</summary>
    public static class BackoffPolicy
    {
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        /// <param name="attempt">연속 실패 횟수 (1부터).</param>
        public static TimeSpan NextDelay(int attempt)
        {
            if (attempt < 1) attempt = 1;
            double seconds = Math.Pow(2, attempt - 1); // 1, 2, 4, 8, ...
            return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
        }
    }
}
