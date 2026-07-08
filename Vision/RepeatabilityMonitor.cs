using BODA.CMS.Analytics;

namespace BODA.CMS.Vision
{
    /// <summary>비전 감시 파라미터.</summary>
    public sealed record VisionOptions
    {
        public static VisionOptions Default { get; } = new();

        /// <summary>픽셀 → mm 환산 (카메라 캘리브레이션). PoC는 상수 스케일.</summary>
        public double MmPerPixel { get; init; } = 0.05;

        /// <summary>기준선 학습에 쓰는 사이클 수 (건강한 상태의 정지 촬영).</summary>
        public int BaselineCycles { get; init; } = 30;

        /// <summary>반복정밀도 드리프트 z-임계(위치 축별) + 디바운스.</summary>
        public double DriftZ { get; init; } = 4.0;
        public int DriftDebounce { get; init; } = 3;

        /// <summary>마모(지름 감소) z-임계 + 디바운스 — 감소 방향만 본다.</summary>
        public double WearZ { get; init; } = 4.0;
        public int WearDebounce { get; init; } = 3;

        /// <summary>해제: |z|가 이 값 미만이 연속되면 복귀 통지.</summary>
        public double ResolveZ { get; init; } = 1.5;
        public int ResolveDebounce { get; init; } = 3;

        /// <summary>σ 하한(px) — 완전 정적 장면의 과민 알람 방지.</summary>
        public double MinStdPx { get; init; } = 0.02;
    }

    public sealed record VisionSnapshot(
        CbmPhase Phase,
        double LearningProgress,
        double LastRadialMm,      // 기준점 대비 반경 방향 이탈(mm)
        double LastDiameterMm,
        double WorstZ,
        int ActiveAlertCount);

    /// <summary>
    /// 반복정밀도 드리프트 + 엔드이펙터 마모(마커 지름 감소) 감시 (ROADMAP §4 P4 PoC).
    ///
    /// 사용법: 로봇이 사이클마다 같은 자세로 정지했을 때 촬영한 마커 관측을 <see cref="Ingest"/>로 공급.
    /// 초기 <see cref="VisionOptions.BaselineCycles"/>회는 기준선(중심 x/y·지름 평균/σ) 학습, 이후
    /// z-판정으로 위치 드리프트(양방향)·지름 감소(단방향)를 감시한다 — P2 CBM과 같은 통계 기법.
    /// 알림은 <see cref="CbmAlert"/>로 발화해 기존 대시보드/알림 채널에 그대로 융합된다.
    /// </summary>
    public sealed class RepeatabilityMonitor
    {
        private readonly string _stationId;
        private readonly VisionOptions _options;
        private readonly RunningStats _bx = new();
        private readonly RunningStats _by = new();
        private readonly RunningStats _bd = new();

        private int _driftStreakX, _driftStreakY, _wearStreak, _normalStreak;
        private bool _driftActive, _wearActive;
        private double _lastRadialMm, _lastDiameterMm, _worstZ;

        public RepeatabilityMonitor(string stationId, VisionOptions? options = null)
        {
            _stationId = stationId;
            _options = options ?? VisionOptions.Default;
        }

        public event Action<CbmAlert>? AlertRaised;

        public bool IsLearned => _bx.Count >= _options.BaselineCycles;

        public VisionSnapshot Snapshot => new(
            IsLearned ? CbmPhase.Monitoring : CbmPhase.Learning,
            Math.Min(1.0, (double)_bx.Count / _options.BaselineCycles),
            _lastRadialMm,
            _lastDiameterMm,
            _worstZ,
            (_driftActive ? 1 : 0) + (_wearActive ? 1 : 0));

        /// <summary>사이클 1회의 마커 관측 반영. 학습 완료 후에는 판정·알림 수행.</summary>
        public void Ingest(MarkerObservation obs)
        {
            _lastDiameterMm = obs.DiameterPx * _options.MmPerPixel;

            if (!IsLearned)
            {
                _bx.Add(obs.CxPx);
                _by.Add(obs.CyPx);
                _bd.Add(obs.DiameterPx);
                return;
            }

            double sx = Math.Max(_bx.Std, _options.MinStdPx);
            double sy = Math.Max(_by.Std, _options.MinStdPx);
            double sd = Math.Max(_bd.Std, _options.MinStdPx);

            double zx = (obs.CxPx - _bx.Mean) / sx;
            double zy = (obs.CyPx - _by.Mean) / sy;
            double zd = (_bd.Mean - obs.DiameterPx) / sd; // 감소 방향이 양수 = 마모 의심

            double dxMm = (obs.CxPx - _bx.Mean) * _options.MmPerPixel;
            double dyMm = (obs.CyPx - _by.Mean) * _options.MmPerPixel;
            _lastRadialMm = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
            _worstZ = Math.Max(Math.Max(Math.Abs(zx), Math.Abs(zy)), Math.Max(zd, 0));

            var alerts = new List<CbmAlert>();

            // 반복정밀도 드리프트 (양방향, 축별 디바운스 후 통합 1건)
            _driftStreakX = Math.Abs(zx) >= _options.DriftZ ? _driftStreakX + 1 : 0;
            _driftStreakY = Math.Abs(zy) >= _options.DriftZ ? _driftStreakY + 1 : 0;
            if (!_driftActive && (_driftStreakX >= _options.DriftDebounce || _driftStreakY >= _options.DriftDebounce))
            {
                _driftActive = true;
                alerts.Add(NewAlert(CbmSeverity.Alarm, "비전 드리프트", Math.Abs(zx) >= Math.Abs(zy) ? zx : zy,
                    $"[{_stationId}] 반복정밀도 드리프트: 이탈 {_lastRadialMm:0.000}mm " +
                    $"(zx={zx:0.0}, zy={zy:0.0}, 기준 ±{sx * _options.MmPerPixel:0.000}mm)"));
            }

            // 마모 의심 (지름 감소 단방향)
            _wearStreak = zd >= _options.WearZ ? _wearStreak + 1 : 0;
            if (!_wearActive && _wearStreak >= _options.WearDebounce)
            {
                _wearActive = true;
                alerts.Add(NewAlert(CbmSeverity.Warning, "마모 의심", zd,
                    $"[{_stationId}] 마커 지름 감소(마모 의심): {_bd.Mean * _options.MmPerPixel:0.000} → " +
                    $"{_lastDiameterMm:0.000}mm (z={zd:0.0})"));
            }

            // 복귀
            bool stable = Math.Abs(zx) < _options.ResolveZ && Math.Abs(zy) < _options.ResolveZ && zd < _options.ResolveZ;
            if ((_driftActive || _wearActive) && stable)
            {
                if (++_normalStreak >= _options.ResolveDebounce)
                {
                    _driftActive = _wearActive = false;
                    _normalStreak = 0;
                    alerts.Add(NewAlert(CbmSeverity.Info, "비전 복귀", Math.Max(Math.Abs(zx), Math.Abs(zy)),
                        $"[{_stationId}] 비전 측정 정상 복귀 (이탈 {_lastRadialMm:0.000}mm)"));
                }
            }
            else _normalStreak = 0;

            foreach (CbmAlert a in alerts) AlertRaised?.Invoke(a);
        }

        private CbmAlert NewAlert(CbmSeverity severity, string kind, double z, string message) =>
            new(DateTime.UtcNow, VendorId: "vision", ChannelId: _stationId, Signal: "마커",
                AxisIndex: 0, severity, kind, z,
                BaselineMean: _bx.Mean, BaselineStd: _bx.Std, Value: _lastRadialMm,
                CustomMessage: message);
    }
}
