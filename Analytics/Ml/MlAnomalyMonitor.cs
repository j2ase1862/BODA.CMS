namespace BODA.CMS.Analytics.Ml
{
    /// <summary>ML 판정 파라미터.</summary>
    public sealed record MlOptions
    {
        public static MlOptions Default { get; } = new();
        /// <summary>임계 미달이 연속 이 횟수면 알림 (집계 단위).</summary>
        public int Debounce { get; init; } = 3;
        /// <summary>정상 점수가 연속 이 횟수면 해제 통지.</summary>
        public int ResolveDebounce { get; init; } = 5;
    }

    /// <summary>ML 이상탐지 현재 상태 (UI 폴링용).</summary>
    public sealed record MlSnapshot(
        long ScoredWindows,
        int ActiveAlertCount,
        double WorstMargin,        // min(score-threshold) — 음수면 이상 구간
        string? WorstDescription);

    /// <summary>
    /// ML 이상탐지 모니터 — CBM 집계 스트림(z)을 구독해 신호·축별 슬라이딩 윈도 피처를
    /// ONNX 모델로 스코어링하고, 임계 미달(디바운스)이면 알림을 낸다 (ROADMAP P3).
    ///
    /// CBM(규칙 기반)과의 관계: CBM은 단일 신호의 편차/추세를, ML은 윈도의 <i>형태</i>
    /// (진동·요동·계단 등 6피처 조합)를 본다 — 상호 보완이며 알림 채널은 공유한다.
    /// </summary>
    public sealed class MlAnomalyMonitor : IDisposable
    {
        public const string OnnxFileName = "anomaly_iforest.onnx";
        public const string SidecarFileName = "anomaly_iforest.json";

        private sealed class AxisState
        {
            public readonly Queue<double> Window = new();
            public double LastScore = double.MaxValue;
            public int OutStreak;
            public int NormalStreak;
            public bool Active;
        }

        private readonly object _gate = new();
        private readonly IAnomalyScorer _scorer;
        private readonly MlModelInfo _info;
        private readonly MlOptions _options;
        private readonly Dictionary<(string Signal, int Axis), AxisState> _states = new();
        private long _scoredWindows;

        public MlAnomalyMonitor(IAnomalyScorer scorer, MlModelInfo info, MlOptions? options = null)
        {
            _scorer = scorer;
            _info = info;
            _options = options ?? MlOptions.Default;
        }

        /// <summary>모델 디렉터리에서 로드. 모델이 없으면 null — 호출부는 ML 기능을 끈 채 동작한다.</summary>
        public static MlAnomalyMonitor? TryLoad(string modelDirectory, MlOptions? options = null)
        {
            string onnx = Path.Combine(modelDirectory, OnnxFileName);
            string sidecar = Path.Combine(modelDirectory, SidecarFileName);
            if (!File.Exists(onnx) || !File.Exists(sidecar)) return null;
            return new MlAnomalyMonitor(new OnnxAnomalyScorer(onnx), MlModelInfo.Load(sidecar), options);
        }

        /// <summary>알림 발생/해제. CBM Ingest 호출 스레드에서 발화 — UI 마샬링은 구독자 책임.</summary>
        public event Action<CbmAlert>? AlertRaised;

        public void Attach(CbmMonitor cbm) => cbm.AggregateEvaluated += OnAggregate;

        public MlSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    double worst = double.MaxValue;
                    string? desc = null;
                    int active = 0;
                    foreach (((string signal, int axis), AxisState s) in _states)
                    {
                        if (s.LastScore == double.MaxValue) continue;
                        double margin = s.LastScore - _info.Threshold;
                        if (margin < worst)
                        {
                            worst = margin;
                            desc = $"J{axis + 1} {signal} score={s.LastScore:0.000}";
                        }
                        if (s.Active) active++;
                    }
                    return new MlSnapshot(_scoredWindows, active,
                        worst == double.MaxValue ? 0 : worst, desc);
                }
            }
        }

        private void OnAggregate(CbmAggregate agg)
        {
            if (agg.Z is not double z) return; // 기준선 학습 중 — z가 없으면 피처도 없다

            CbmAlert? alert = null;
            lock (_gate)
            {
                if (!_states.TryGetValue((agg.Signal, agg.Axis), out AxisState? s))
                    _states[(agg.Signal, agg.Axis)] = s = new AxisState();

                s.Window.Enqueue(z);
                if (s.Window.Count < _info.Window) return;

                double score = _scorer.Score(AnomalyFeatures.Compute(s.Window.ToArray()));
                s.Window.Dequeue(); // 슬라이딩
                s.LastScore = score;
                _scoredWindows++;

                if (score < _info.Threshold)
                {
                    s.NormalStreak = 0;
                    if (++s.OutStreak >= _options.Debounce && !s.Active)
                    {
                        s.Active = true;
                        alert = NewAlert(agg, CbmSeverity.Warning, "ML 이상", score,
                            $"J{agg.Axis + 1} {agg.Signal} ML 이상 탐지: score={score:0.000} (임계 {_info.Threshold:0.000})");
                    }
                }
                else
                {
                    s.OutStreak = 0;
                    if (s.Active && ++s.NormalStreak >= _options.ResolveDebounce)
                    {
                        s.Active = false;
                        s.NormalStreak = 0;
                        alert = NewAlert(agg, CbmSeverity.Info, "ML 복귀", score,
                            $"J{agg.Axis + 1} {agg.Signal} ML 정상 복귀: score={score:0.000}");
                    }
                }
            }

            if (alert is not null) AlertRaised?.Invoke(alert);
        }

        private CbmAlert NewAlert(CbmAggregate agg, CbmSeverity severity, string kind, double score, string message) =>
            new(DateTime.UtcNow, agg.VendorId, agg.ChannelId, agg.Signal, agg.Axis,
                severity, kind, Z: score - _info.Threshold, BaselineMean: _info.Threshold,
                BaselineStd: 0, Value: score, CustomMessage: message);

        public void Dispose() => (_scorer as IDisposable)?.Dispose();
    }
}
