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
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Mvvm;

namespace BODA.CMS.ViewModels
{
    /// <summary>
    /// 텔레메트리 소스 1개(= 채널 카드 1장)의 화면 상태.
    /// <see cref="IRobotTelemetrySource"/> 계약에만 의존한다 — 벤더가 늘어나도 이 클래스와 XAML은 불변.
    /// </summary>
    public sealed class TelemetrySourceViewModel : ViewModelBase
    {
        // 드라이버 프레임은 최대 ~100Hz — UI는 이 간격으로만 갱신(throttle).
        private static readonly TimeSpan UiThrottle = TimeSpan.FromMilliseconds(100);

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

        private string _status = "대기";
        private Brush _statusBrush = Brushes.Gray;
        private string _readout = "(모니터링 시작 전)";
        private string _port;
        private bool _isRunning;
        private bool _isChartMode;
        private SignalToggle? _selectedChartSignal;

        public TelemetrySourceViewModel(IRobotTelemetrySource source, Func<string> getHost, Action<string> log)
        {
            _source = source;
            _getHost = getHost;
            _log = log;
            _port = source.Capabilities.DefaultPort.ToString(CultureInfo.InvariantCulture);

            ToggleCommand = new AsyncRelayCommand(ToggleAsync);

            _source.FrameReceived += OnFrameReceived;
            _source.StateChanged += OnStateChanged;
            _source.Notification += (_, msg) => _log($"[{Title}] {msg}");
        }

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

        /// <summary>true면 판독 표 대신 라이브 차트를 표시.</summary>
        public bool IsChartMode { get => _isChartMode; set => SetProperty(ref _isChartMode, value); }

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

            string host = _getHost().Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                Status = "IP 주소를 입력하세요";
                StatusBrush = Brushes.OrangeRed;
                return;
            }
            if (!int.TryParse(Port, out int port) || port < 1 || port > 65535)
            {
                Status = "포트 값이 올바르지 않습니다";
                StatusBrush = Brushes.OrangeRed;
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
                StatusBrush = Brushes.Firebrick;
                _log($"[{Title}] 네이티브 DLL을 찾을 수 없습니다. exe 폴더의 DLL 번들을 확인하세요.");
            }
            catch (Exception ex)
            {
                Status = "연결 실패";
                StatusBrush = Brushes.Firebrick;
                _log($"[{Title}] 연결 실패: " + ex.GetBaseException().Message);
            }
        }

        // ⚠️ 드라이버 스레드에서 호출 → UI 스레드로 마샬링.
        private void OnStateChanged(object? sender, TelemetrySourceState state) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                (Status, StatusBrush, IsRunning) = state switch
                {
                    TelemetrySourceState.Connecting => ("연결 중...", (Brush)Brushes.DarkOrange, false),
                    TelemetrySourceState.Connected => ("수신 중", Brushes.SeaGreen, true),
                    TelemetrySourceState.Faulted => ("연결 끊김", Brushes.Firebrick, false),
                    _ => ("대기", Brushes.Gray, false),
                };
            });

        // ⚠️ 드라이버 스레드에서 호출 → throttle 후 UI 스레드로 마샬링.
        // 프레임은 불변이라 스레드 간 전달이 안전하다.
        private void OnFrameReceived(object? sender, RobotTelemetryFrame frame)
        {
            // 차트 경로: throttle 이전에 전 샘플을 큐잉 (상한 도달 시 드롭).
            if (_chartQueueCount < ChartQueueLimit)
            {
                _chartQueue.Enqueue(frame);
                Interlocked.Increment(ref _chartQueueCount);
            }

            DateTime now = DateTime.UtcNow;
            if (now - _lastUiUpdate < UiThrottle) return;
            _lastUiUpdate = now;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _lastFrame = frame;
                EnsureSignalToggles(frame);
                Readout = BuildReadout(frame, SelectedLabels());
            });
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

        /// <summary>프레임의 표시 가능한 행(라벨 + 셀 문자열) 열거 — 토글 구성과 판독 출력의 단일 원천.</summary>
        private static IEnumerable<(string Label, string Cells)> Rows(RobotTelemetryFrame f)
        {
            foreach ((string label, double[] values, string fmt) in EnumerateSignals(f))
                yield return (label, string.Join(" ", values.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(9))));
        }

        /// <summary>
        /// 프레임에 존재하는 신호(라벨 + 축별 값 + 표시 형식) 열거 — 판독 표와 차트가 공유하는 단일 원천.
        /// 라벨은 신호 토글·차트 신호 선택의 키와 동일.
        /// </summary>
        internal static IEnumerable<(string Label, double[] Values, string Format)> EnumerateSignals(RobotTelemetryFrame f)
        {
            static double[] D(float[] v) => Array.ConvertAll(v, x => (double)x);

            yield return ("위치°", D(f.JointPositionDeg), "0.00");

            if (f.JointVelocityDegS is { } vel) yield return ("속도°/s", D(vel), "0.0");
            if (f.JointTorqueNm is { } jts) yield return ("토크Nm", D(jts), "0.00");
            if (f.ModelTorqueNm is { } mdl) yield return ("모델Nm", D(mdl), "0.00");
            if (f.ExternalTorqueNm is { } ext) yield return ("외란Nm", D(ext), "0.00");
            if (f.MotorCurrentA is { } cur) yield return ("전류A", D(cur), "0.00");
            if (f.TemperatureC is { } tmp) yield return ("온도℃", D(tmp), "0.0");

            if (f.VendorRaw is not null)
                foreach ((string key, double[] raw) in f.VendorRaw)
                    yield return (key, raw, "0");
        }

        /// <summary>프레임에서 라벨에 해당하는 축별 값 추출 (차트용). 없으면 null.</summary>
        internal static double[]? ExtractSignal(RobotTelemetryFrame f, string label)
        {
            foreach ((string l, double[] values, _) in EnumerateSignals(f))
                if (l == label) return values;
            return null;
        }
    }
}
