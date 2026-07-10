using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BODA.CMS.Analytics;
using BODA.CMS.Analytics.Ml;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Mvvm;

namespace BODA.CMS.ViewModels
{
    /// <summary>채널 카드의 본문 표시 모드.</summary>
    public enum CardView { Table, Chart, Robot }

    /// <summary>
    /// 텔레메트리 소스 1개(= 채널 카드 1장)의 화면 상태.
    /// <see cref="IRobotTelemetrySource"/> 계약에만 의존한다 — 벤더가 늘어나도 이 클래스와 XAML은 불변.
    /// </summary>
    public sealed class TelemetrySourceViewModel : ViewModelBase
    {
        // 드라이버 프레임은 최대 ~100Hz — 판독 표는 이 간격으로만 갱신(throttle).
        // 5Hz: 눈으로 보는 수치 갱신에 충분하고 저사양 PC의 UI 스레드 부담이 절반.
        private static readonly TimeSpan UiThrottle = TimeSpan.FromMilliseconds(200);

        private readonly IRobotTelemetrySource _source;
        private readonly Func<string> _getHost;
        private readonly Action<string> _log;
        private readonly Dictionary<string, SignalToggle> _signalIndex = new();
        private DateTime _lastUiUpdate = DateTime.MinValue;
        private RobotTelemetryFrame? _lastFrame; // UI 스레드에서만 접근

        // 차트는 표(10Hz throttle)와 달리 전 샘플이 필요 — 드라이버 스레드가 큐에 넣고
        // 차트 뷰(SignalChartView)가 UI 타이머로 드레인한다. 소비가 멈춰도 상한에서 드롭.
        private const int ChartQueueLimit = 2048;
        private readonly ConcurrentQueue<RobotTelemetryFrame> _chartQueue = new();
        private int _chartQueueCount;

        private readonly CbmMonitor _cbm = new();
        private volatile MlAnomalyMonitor? _ml; // 백그라운드 로드 완료 후 세팅 (콜드 스타트 10초+ — UI 블록 금지)

        private string _status = "대기";
        private Brush _statusBrush = Theme.Muted;
        private string _readout = "(모니터링 시작 전)";
        private string _port;
        private bool _isRunning;
        private CardView _view = CardView.Table;
        private int _healthScore = 100;
        private SignalToggle? _selectedChartSignal;
        private string _cbmText = "CBM 대기";
        private Brush _cbmBrush = Theme.Muted;
        private string _mlText = "";
        private Brush _mlBrush = Theme.Muted;

        private readonly Func<ProductTier, bool>? _canUseTier;

        public TelemetrySourceViewModel(
            IRobotTelemetrySource source, Func<string> getHost, Action<string> log,
            Action<string, CbmAlert>? onCbmAlert = null,
            Func<ProductTier, bool>? canUseTier = null)
        {
            _source = source;
            _getHost = getHost;
            _log = log;
            _canUseTier = canUseTier;
            _port = source.Capabilities.DefaultPort.ToString(CultureInfo.InvariantCulture);

            ToggleCommand = new AsyncRelayCommand(ToggleAsync);

            _source.FrameReceived += OnFrameReceived;
            _source.StateChanged += OnStateChanged;
            _source.Notification += (_, msg) => _log($"[{Title}] {msg}");
            if (onCbmAlert is not null)
                _cbm.AlertRaised += a => onCbmAlert(Title, a); // ⚠️ 드라이버 스레드 — 구독자가 마샬링

            // ML 이상탐지: 모델이 있으면 CBM 집계 스트림에 연결 (없으면 CBM만으로 동작).
            // ONNX 세션 로드는 콜드 스타트에서 10초+ 걸릴 수 있어 백그라운드에서 — UI 기동을 막지 않는다.
            _mlText = "ML 로드 중…";
            _ = Task.Run(() =>
            {
                MlAnomalyMonitor? ml = MlAnomalyMonitor.TryLoad(System.IO.Path.Combine(AppContext.BaseDirectory, "models"));
                if (ml is not null)
                {
                    ml.Attach(_cbm);
                    if (onCbmAlert is not null) ml.AlertRaised += a => onCbmAlert(Title, a);
                }
                _ml = ml; // 배선 완료 후 공개 — 이후 프레임부터 ML 판정 시작
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ml is null) { MlText = "ML 모델 없음"; MlBrush = Theme.Muted; }
                    else if (MlText == "ML 로드 중…") { MlText = "ML 대기"; MlBrush = Theme.Muted; }
                });
            });
        }

        /// <summary>벤더 전환 등으로 카드가 폐기될 때 네이티브 자원(ONNX 세션) 해제.</summary>
        public void Cleanup() => _ml?.Dispose();

        public IRobotTelemetrySource Source => _source;

        public ProductTier Tier => ProductTierEvaluator.Evaluate(_source.Capabilities);

        public string Title => _source.Capabilities.DisplayName;

        public string SubLabel
        {
            get
            {
                RobotCapabilities c = _source.Capabilities;
                return $"({Tier} · {c.NominalSampleRateHz:0}Hz · {(c.IsPassive ? "패시브" : "능동")})";
            }
        }

        /// <summary>
        /// 표시 신호 선택 — 프레임에 실제로 존재하는 신호로 자동 구성(첫 수신 시).
        /// 벤더별 하드코딩 없음: 라벨은 판독 행과 동일한 키.
        /// </summary>
        public ObservableCollection<SignalToggle> Signals { get; } = new();

        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public Brush StatusBrush { get => _statusBrush; set => SetProperty(ref _statusBrush, value); }
        public string Readout { get => _readout; set => SetProperty(ref _readout, value); }
        public string Port { get => _port; set => SetProperty(ref _port, value); }

        public bool IsRunning
        {
            get => _isRunning;
            set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(ButtonText)); }
        }

        public string ButtonText => IsRunning ? "모니터링 중지" : "모니터링 시작";

        /// <summary>CBM 상태 칩: "CBM 학습 42%" → "건강도 100" (알림 있으면 개수 표시).</summary>
        public string CbmText { get => _cbmText; set => SetProperty(ref _cbmText, value); }
        public Brush CbmBrush { get => _cbmBrush; set => SetProperty(ref _cbmBrush, value); }

        /// <summary>ML 이상탐지 칩: "ML 정상" / "ML 이상 n건". 모델 없으면 안내만.</summary>
        public string MlText { get => _mlText; set => SetProperty(ref _mlText, value); }
        public Brush MlBrush { get => _mlBrush; set => SetProperty(ref _mlBrush, value); }

        /// <summary>본문 표시 모드 (표 / 차트 / 로봇) — 세그먼트 버튼 바인딩.</summary>
        public CardView View
        {
            get => _view;
            set { if (SetProperty(ref _view, value)) OnPropertyChanged(nameof(IsChartMode)); }
        }

        /// <summary>차트 드레인 여부 판단용 (SignalChartView) — View 파생.</summary>
        public bool IsChartMode => _view == CardView.Chart;

        /// <summary>CBM 건강도 0~100 — 카드 헤더 게이지 바인딩 (학습 중엔 100 유지·색으로 구분).</summary>
        public int HealthScore { get => _healthScore; set => SetProperty(ref _healthScore, value); }

        /// <summary>마지막 수신 프레임 (UI 스레드에서만 접근) — 로봇 스켈레톤 뷰가 5Hz 타이머로 읽는다.</summary>
        public RobotTelemetryFrame? LastFrame => _lastFrame;

        /// <summary>CBM 신호·축별 현재 z 상태 — 스켈레톤 관절 색·히트맵용 (호출 시점 스냅샷).</summary>
        public IReadOnlyList<CbmAxisDetail> CbmDetails => _cbm.DetailSnapshot;

        /// <summary>차트에 그릴 신호(한 번에 1신호 = 축별 6라인). 첫 프레임 수신 시 첫 신호로 자동 설정.</summary>
        public SignalToggle? SelectedChartSignal { get => _selectedChartSignal; set => SetProperty(ref _selectedChartSignal, value); }

        public AsyncRelayCommand ToggleCommand { get; }

        /// <summary>차트용 프레임 드레인 — SignalChartView의 UI 타이머에서 호출.</summary>
        public bool TryDequeueChartFrame(out RobotTelemetryFrame frame)
        {
            if (_chartQueue.TryDequeue(out frame!))
            {
                Interlocked.Decrement(ref _chartQueueCount);
                return true;
            }
            return false;
        }

        /// <summary>실행 중이면 중지(외부에서 일괄 정리할 때 사용 — 연결 카드 해제·제조사 전환 등).</summary>
        public async Task StopAsync()
        {
            if (!IsRunning) return;
            await _source.DisconnectAsync();
            _log($"[{Title}] 모니터링을 중지했습니다.");
        }

        private async Task ToggleAsync()
        {
            if (IsRunning)
            {
                await StopAsync();
                return;
            }

            // 라이선스 게이팅 (P5) — 등급은 capability 자동 판정과 연동.
            if (_canUseTier is not null && !_canUseTier(Tier))
            {
                Status = "라이선스 등급 초과";
                StatusBrush = Theme.Bad;
                _log($"[{Title}] 라이선스 등급으로 {Tier} 채널을 사용할 수 없습니다 — 업그레이드 필요.");
                return;
            }

            string host = _getHost().Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                Status = "IP 주소를 입력하세요";
                StatusBrush = Theme.Bad;
                return;
            }
            if (!int.TryParse(Port, out int port) || port < 1 || port > 65535)
            {
                Status = "포트 값이 올바르지 않습니다";
                StatusBrush = Theme.Bad;
                return;
            }

            try
            {
                _log($"[{Title}] 연결 시도: {host}:{port}");
                await _source.ConnectAsync(new RobotEndpoint(host, port));
            }
            catch (DllNotFoundException)
            {
                Status = "네이티브 DLL 없음";
                StatusBrush = Theme.Bad;
                _log($"[{Title}] 네이티브 DLL을 찾을 수 없습니다. exe 폴더의 DLL 번들을 확인하세요.");
            }
            catch (Exception ex)
            {
                Status = "연결 실패";
                StatusBrush = Theme.Bad;
                _log($"[{Title}] 연결 실패: " + ex.GetBaseException().Message);
            }
        }

        // ⚠️ 드라이버 스레드에서 호출 → UI 스레드로 마샬링.
        private void OnStateChanged(object? sender, TelemetrySourceState state) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                (Status, StatusBrush, IsRunning) = state switch
                {
                    TelemetrySourceState.Connecting => ("연결 중...", (Brush)Theme.Warn, false),
                    TelemetrySourceState.Connected => ("수신 중", Theme.Ok, true),
                    TelemetrySourceState.Faulted => ("연결 끊김", Theme.Bad, false),
                    _ => ("대기", Theme.Muted, false),
                };
            });

        // ⚠️ 드라이버 스레드에서 호출 → throttle 후 UI 스레드로 마샬링.
        // 프레임은 불변이라 스레드 간 전달이 안전하다.
        private void OnFrameReceived(object? sender, RobotTelemetryFrame frame)
        {
            // CBM 경로: 전 샘플 반영 (내부 1초 집계 — 드라이버 스레드 안전).
            _cbm.Ingest(frame);

            // 차트 경로: throttle 이전에 전 샘플을 큐잉 (상한 도달 시 드롭).
            if (_chartQueueCount < ChartQueueLimit)
            {
                _chartQueue.Enqueue(frame);
                Interlocked.Increment(ref _chartQueueCount);
            }

            DateTime now = DateTime.UtcNow;
            if (now - _lastUiUpdate < UiThrottle) return;
            _lastUiUpdate = now;

            CbmSnapshot cbm = _cbm.Snapshot;
            MlSnapshot? ml = _ml?.Snapshot;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _lastFrame = frame;
                EnsureSignalToggles(frame);
                Readout = BuildReadout(frame, SelectedLabels());
                UpdateCbmChip(cbm);
                if (ml is not null) UpdateMlChip(ml);
            });
        }

        private void UpdateMlChip(MlSnapshot s)
        {
            if (s.ScoredWindows == 0)
            {
                MlText = "ML 대기";
                MlBrush = Theme.Muted;
                return;
            }

            MlText = s.ActiveAlertCount > 0
                ? $"ML 이상 {s.ActiveAlertCount}건 ({s.WorstDescription})"
                : "ML 정상";
            MlBrush = s.ActiveAlertCount > 0 ? Theme.Bad : Theme.Ok;
        }

        private void UpdateCbmChip(CbmSnapshot s)
        {
            if (s.Phase == CbmPhase.Learning)
            {
                CbmText = $"CBM 학습 {s.LearningProgress:P0}";
                CbmBrush = Theme.Muted;
                HealthScore = 100;
                return;
            }

            HealthScore = s.HealthScore;
            CbmText = s.ActiveAlertCount > 0
                ? $"건강도 {s.HealthScore} · 알림 {s.ActiveAlertCount} ({s.WorstDescription})"
                : $"건강도 {s.HealthScore}";
            CbmBrush = s.HealthScore >= 80 ? Theme.Ok
                     : s.HealthScore >= 50 ? Theme.Warn
                     : Theme.Bad;
        }

        /// <summary>프레임에 존재하는 신호마다 토글을 1회 생성(라벨 = 판독 행 키).</summary>
        private void EnsureSignalToggles(RobotTelemetryFrame frame)
        {
            foreach ((string label, _) in Rows(frame))
            {
                if (_signalIndex.ContainsKey(label)) continue;

                var toggle = new SignalToggle(label);
                toggle.PropertyChanged += (_, _) => RefreshReadout();
                _signalIndex[label] = toggle;
                Signals.Add(toggle);
                SelectedChartSignal ??= toggle; // 차트 기본 신호 = 첫 신호(위치°)
            }
        }

        // 체크박스 조작 시 마지막 프레임으로 즉시 다시 그림 (다음 프레임을 기다리지 않음).
        private void RefreshReadout()
        {
            if (_lastFrame is not null)
                Readout = BuildReadout(_lastFrame, SelectedLabels());
        }

        private HashSet<string> SelectedLabels() =>
            _signalIndex.Values.Where(t => t.IsSelected).Select(t => t.Label).ToHashSet();

        /// <summary>
        /// 프레임에 존재하는 신호만 행으로 출력 — 벤더/채널 무관 공용 렌더러.
        /// <paramref name="visible"/>이 null이면 전체 표시, 아니면 포함된 라벨만.
        /// </summary>
        internal static string BuildReadout(RobotTelemetryFrame f, ISet<string>? visible = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("축        " + string.Join(" ", Enumerable.Range(1, f.AxisCount).Select(i => $"J{i}".PadLeft(9))));

            int rows = 0;
            foreach ((string label, string cells) in Rows(f))
            {
                if (visible is not null && !visible.Contains(label)) continue;
                sb.AppendLine($"{label,-8}" + cells);
                rows++;
            }

            if (rows == 0) sb.AppendLine("(표시할 신호가 선택되지 않았습니다)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>프레임의 표시 가능한 행(라벨 + 셀 문자열) 열거 — 신호 열거 원천은 Core의 <see cref="TelemetrySignals"/>.</summary>
        private static IEnumerable<(string Label, string Cells)> Rows(RobotTelemetryFrame f)
        {
            foreach ((string label, double[] values, string fmt) in TelemetrySignals.Enumerate(f))
                yield return (label, string.Join(" ", values.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(9))));
        }
    }
}
