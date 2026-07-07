using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Analytics
{
    /// <summary>
    /// 조건기반 감시(CBM) 엔진 — 채널(소스) 1개당 1인스턴스.
    ///
    /// 동작: 프레임을 <see cref="CbmOptions.AggregationSeconds"/> 창 평균으로 집계 →
    /// 신호·축별 기준선(평균/σ)을 <see cref="CbmOptions.LearningAggregates"/>개 학습 →
    /// 이후 z-점수 기반 급변(즉시값)·드리프트(EWMA) 판정, 디바운스 후 알림, 정상 복귀 시 해제 통지.
    ///
    /// 스레드: <see cref="Ingest"/>는 드라이버 스레드에서 호출돼도 안전(내부 lock).
    /// <see cref="AlertRaised"/>는 Ingest 호출 스레드에서 발화 — UI 마샬링은 구독자 책임.
    /// </summary>
    public sealed class CbmMonitor
    {
        private sealed class AxisState
        {
            public readonly RunningStats Baseline = new();
            public double Ewma;
            public bool EwmaInitialized;
            public double LastZ;
            public double LastDriftZ;
            public int SpikeStreak;
            public int DriftStreak;
            public int NormalStreak;
            public bool SpikeActive;
            public bool DriftActive;
            public bool IsLearned(CbmOptions o) => Baseline.Count >= o.LearningAggregates;
            public bool AlertActive => SpikeActive || DriftActive;
        }

        private readonly object _gate = new();
        private readonly CbmOptions _options;
        private readonly Dictionary<(string Signal, int Axis), AxisState> _states = new();
        private readonly Dictionary<(string Signal, int Axis), (double Sum, int N)> _accum = new();
        private DateTime _currentBucket;
        private bool _evaluatedOnce;
        private string _vendorId = "?";
        private string _channelId = "?";

        public CbmMonitor(CbmOptions? options = null) => _options = options ?? CbmOptions.Default;

        /// <summary>알림 발생(급변/드리프트/복귀). Ingest 호출 스레드에서 발화.</summary>
        public event Action<CbmAlert>? AlertRaised;

        /// <summary>프레임 1건 반영. 집계 창이 넘어가면 내부 판정이 수행된다.</summary>
        public void Ingest(RobotTelemetryFrame frame)
        {
            List<CbmAlert>? alerts = null;

            lock (_gate)
            {
                _vendorId = frame.VendorId;
                _channelId = frame.ChannelId;

                DateTime bucket = Truncate(frame.ReceivedAtUtc, _options.AggregationSeconds);
                if (_currentBucket != default && bucket != _currentBucket)
                    alerts = EvaluateBucket(frame.ReceivedAtUtc);
                _currentBucket = bucket;

                foreach ((string label, double[] values, _) in TelemetrySignals.Enumerate(frame))
                {
                    if (_options.ExcludedSignals.Contains(label)) continue;
                    for (int axis = 0; axis < values.Length; axis++)
                    {
                        (double sum, int n) = _accum.GetValueOrDefault((label, axis));
                        _accum[(label, axis)] = (sum + values[axis], n + 1);
                    }
                }
            }

            // 핸들러 재진입/교착 방지를 위해 lock 밖에서 발화.
            if (alerts is not null)
                foreach (CbmAlert a in alerts) AlertRaised?.Invoke(a);
        }

        public CbmSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    if (_states.Count == 0)
                    {
                        return _evaluatedOnce
                            ? new CbmSnapshot(CbmPhase.Monitoring, 1, 100, 0, 0, "감시 신호 없음")
                            : new CbmSnapshot(CbmPhase.Learning, 0, 100, 0, 0, null);
                    }

                    double progress = _states.Values.Average(s =>
                        Math.Min(1.0, (double)s.Baseline.Count / _options.LearningAggregates));
                    bool allLearned = _states.Values.All(s => s.IsLearned(_options));

                    if (!allLearned)
                        return new CbmSnapshot(CbmPhase.Learning, progress, 100, 0, _states.Count, null);

                    double worstZ = 0;
                    string? worst = null;
                    int active = 0;
                    foreach (((string signal, int axis), AxisState s) in _states)
                    {
                        double z = Math.Max(Math.Abs(s.LastZ), Math.Abs(s.LastDriftZ));
                        if (z > worstZ)
                        {
                            worstZ = z;
                            worst = $"J{axis + 1} {signal} z={z:0.0}";
                        }
                        if (s.AlertActive) active++;
                    }

                    // 건강도 휴리스틱: z≤1 → 100점, z≥5 → 0점 (선형).
                    int score = worstZ <= 1 ? 100 : (int)Math.Max(0, Math.Round(100 - 25 * (worstZ - 1)));
                    return new CbmSnapshot(CbmPhase.Monitoring, 1, score, active, _states.Count, worst);
                }
            }
        }

        private List<CbmAlert> EvaluateBucket(DateTime atUtc)
        {
            var alerts = new List<CbmAlert>();
            _evaluatedOnce = true;

            foreach (((string signal, int axis), (double sum, int n)) in _accum)
            {
                if (n == 0) continue;
                double value = sum / n;

                if (!_states.TryGetValue((signal, axis), out AxisState? s))
                    _states[(signal, axis)] = s = new AxisState();

                if (!s.IsLearned(_options))
                {
                    s.Baseline.Add(value);
                    s.Ewma = value; // 학습 중엔 EWMA를 따라가게만
                    s.EwmaInitialized = true;
                    continue;
                }

                double mean = s.Baseline.Mean;
                double std = Math.Max(s.Baseline.Std,
                    Math.Max(_options.AbsoluteMinStd, _options.RelativeMinStd * Math.Abs(mean)));

                double z = (value - mean) / std;
                s.LastZ = z;

                s.Ewma = s.EwmaInitialized
                    ? _options.EwmaAlpha * value + (1 - _options.EwmaAlpha) * s.Ewma
                    : value;
                s.EwmaInitialized = true;
                double driftZ = (s.Ewma - mean) / std;
                s.LastDriftZ = driftZ;

                // 급변(스파이크): 즉시값 z, Alarm.
                if (Math.Abs(z) >= _options.SpikeZ)
                {
                    if (++s.SpikeStreak >= _options.SpikeDebounce && !s.SpikeActive)
                    {
                        s.SpikeActive = true;
                        alerts.Add(NewAlert(atUtc, signal, axis, CbmSeverity.Alarm, "급변", z, mean, std, value));
                    }
                }
                else s.SpikeStreak = 0;

                // 드리프트(추세): EWMA z, Warning.
                if (Math.Abs(driftZ) >= _options.DriftZ)
                {
                    if (++s.DriftStreak >= _options.DriftDebounce && !s.DriftActive)
                    {
                        s.DriftActive = true;
                        alerts.Add(NewAlert(atUtc, signal, axis, CbmSeverity.Warning, "드리프트", driftZ, mean, std, s.Ewma));
                    }
                }
                else s.DriftStreak = 0;

                // 복귀: 활성 알림 중 즉시·추세 z 모두 안정 구간이면 해제 통지.
                if (s.AlertActive && Math.Abs(z) < _options.ResolveZ && Math.Abs(driftZ) < _options.ResolveZ)
                {
                    if (++s.NormalStreak >= _options.ResolveDebounce)
                    {
                        s.SpikeActive = s.DriftActive = false;
                        s.NormalStreak = 0;
                        alerts.Add(NewAlert(atUtc, signal, axis, CbmSeverity.Info, "복귀", z, mean, std, value));
                    }
                }
                else s.NormalStreak = 0;
            }

            _accum.Clear();
            return alerts;
        }

        private CbmAlert NewAlert(DateTime atUtc, string signal, int axis, CbmSeverity severity,
            string kind, double z, double mean, double std, double value) =>
            new(atUtc, _vendorId, _channelId, signal, axis, severity, kind, z, mean, std, value);

        private static DateTime Truncate(DateTime utc, int seconds)
        {
            long ticksPerBucket = TimeSpan.TicksPerSecond * seconds;
            return new DateTime(utc.Ticks - utc.Ticks % ticksPerBucket, DateTimeKind.Utc);
        }
    }
}
