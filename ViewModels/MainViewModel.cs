using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BODA.CMS.Analytics;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Mvvm;
using BODA.CMS.Comms;

namespace BODA.CMS.ViewModels
{
    /// <summary>
    /// 메인 화면 상태: 연결 프로브 카드 + 텔레메트리 소스 카드 목록 + 로그.
    ///
    /// 벤더 격리(ROADMAP §3): 이 클래스는 <see cref="IRobotTelemetrySource"/> 계약에만 의존한다.
    /// 어떤 드라이버를 띄울지는 컴포지션 루트(MainWindow.xaml.cs)가 주입 — 여기에 벤더 분기 금지.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly ModbusConnectionService _probe;
        private readonly StringBuilder _log = new();

        private string _ipAddress = "192.168.137.100"; // 두산 기본 IP. 실제 컨트롤러 IP로 변경
        private string _port = "502";
        private string _statusMessage = "대기 중";
        private Brush _statusBrush = Brushes.Gray;
        private bool _isConnected;
        private string _logText = string.Empty;

        private VendorDescriptor _selectedVendor;

        public MainViewModel(ModbusConnectionService probeConnection, IReadOnlyList<VendorDescriptor> vendors)
        {
            if (vendors.Count == 0) throw new ArgumentException("벤더 카탈로그가 비어 있습니다.", nameof(vendors));

            _probe = probeConnection;
            Vendors = vendors;
            ConnectCommand = new AsyncRelayCommand(ToggleConnectionAsync);

            _selectedVendor = vendors[0];
            LoadSources(_selectedVendor);
        }

        /// <summary>제조사 카탈로그 — 컴포지션 루트가 등록. 새 벤더 = 드라이버 구현 + 카탈로그 1항목.</summary>
        public IReadOnlyList<VendorDescriptor> Vendors { get; }

        /// <summary>선택된 제조사. 변경 시 실행 중인 카드를 정리하고 해당 벤더의 채널 카드로 교체한다.</summary>
        public VendorDescriptor SelectedVendor
        {
            get => _selectedVendor;
            set
            {
                if (value is null || !SetProperty(ref _selectedVendor, value)) return;
                _ = SwitchVendorAsync(value);
            }
        }

        /// <summary>채널 카드 목록 — XAML ItemsControl이 그대로 렌더.</summary>
        public ObservableCollection<TelemetrySourceViewModel> Sources { get; } = new();

        /// <summary>CBM 알림 리스트 (최신이 위, 최대 100건).</summary>
        public ObservableCollection<AlertItem> Alerts { get; } = new();

        // ⚠️ 드라이버 스레드에서 호출 → UI 스레드로 마샬링.
        private void OnCbmAlert(string cardTitle, CbmAlert alert)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(() =>
            {
                Brush brush = alert.Severity switch
                {
                    CbmSeverity.Alarm => Brushes.Firebrick,
                    CbmSeverity.Warning => Brushes.DarkOrange,
                    _ => Brushes.SeaGreen,
                };
                Alerts.Insert(0, new AlertItem($"[{DateTime.Now:HH:mm:ss}] [{cardTitle}] {alert.Message}", brush));
                while (Alerts.Count > 100) Alerts.RemoveAt(Alerts.Count - 1);
                AppendLog($"CBM {alert.Severity}: [{cardTitle}] {alert.Message}");
            });
        }

        private async Task SwitchVendorAsync(VendorDescriptor vendor)
        {
            try
            {
                // 이전 벤더의 소스를 정지·해제한 뒤 교체 (실행 중 전환 안전).
                foreach (TelemetrySourceViewModel s in Sources)
                {
                    await s.StopAsync();
                    await s.Source.DisposeAsync();
                    s.Cleanup();
                }
                Sources.Clear();
                LoadSources(vendor);
                AppendLog($"제조사 전환: {vendor.DisplayName} — 채널 {Sources.Count}개 로드.");
            }
            catch (Exception ex)
            {
                AppendLog("제조사 전환 실패: " + ex.GetBaseException().Message);
            }
        }

        private void LoadSources(VendorDescriptor vendor)
        {
            foreach (IRobotTelemetrySource source in vendor.CreateSources())
                Sources.Add(new TelemetrySourceViewModel(source, () => IpAddress, AppendLogSafe, OnCbmAlert));
        }

        public string IpAddress { get => _ipAddress; set => SetProperty(ref _ipAddress, value); }
        public string Port { get => _port; set => SetProperty(ref _port, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public Brush StatusBrush { get => _statusBrush; set => SetProperty(ref _statusBrush, value); }
        public string LogText { get => _logText; set => SetProperty(ref _logText, value); }

        public bool IsConnected
        {
            get => _isConnected;
            set { if (SetProperty(ref _isConnected, value)) OnPropertyChanged(nameof(ConnectButtonText)); }
        }

        public string ConnectButtonText => IsConnected ? "연결 해제" : "연결";

        public AsyncRelayCommand ConnectCommand { get; }

        // ── 연결 프로브: TCP/Modbus 링크 테스트 (시험 읽기 포함) ──
        private async Task ToggleConnectionAsync()
        {
            if (IsConnected)
            {
                // 프로브 세션을 공유하는 소스가 있을 수 있으니 실행 중인 카드를 먼저 정리.
                foreach (TelemetrySourceViewModel s in Sources)
                    await s.StopAsync();

                _probe.Disconnect();
                IsConnected = false;
                SetStatus("연결 해제됨", Brushes.Gray);
                AppendLog("연결을 해제했습니다.");
                return;
            }

            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                SetStatus("IP 주소를 입력하세요", Brushes.OrangeRed);
                return;
            }
            if (!int.TryParse(Port, out int port) || port < 1 || port > 65535)
            {
                SetStatus("포트 값이 올바르지 않습니다", Brushes.OrangeRed);
                return;
            }

            try
            {
                SetStatus("연결 중...", Brushes.DarkOrange);
                AppendLog($"연결 시도: {IpAddress.Trim()}:{port}");

                await _probe.ConnectAsync(IpAddress.Trim(), port);

                IsConnected = true;
                SetStatus("연결 성공", Brushes.SeaGreen);
                AppendLog("TCP 연결 성공.");

                // 시험 읽기(선택): 홀딩 레지스터 40001~ 4개. 실패해도 연결은 정상일 수 있음.
                try
                {
                    ushort[] regs = await _probe.TryReadHoldingAsync(unitId: 1, start: 0, count: 4);
                    AppendLog("시험 읽기 성공 (40001~): " + string.Join(", ", regs));
                }
                catch (Exception readEx)
                {
                    AppendLog("시험 읽기 실패 (TCP 연결은 정상): " + readEx.GetBaseException().Message);
                    AppendLog("→ UnitId(0/1/255)나 주소 범위를 조정해 보세요.");
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                SetStatus("연결 실패", Brushes.Firebrick);
                AppendLog("연결 실패: " + ex.GetBaseException().Message);
            }
        }

        private void SetStatus(string message, Brush brush)
        {
            StatusMessage = message;
            StatusBrush = brush;
        }

        private void AppendLog(string line)
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            LogText = _log.ToString();
        }

        // 드라이버 스레드에서 올라오는 통지도 받으므로 UI 스레드로 마샬링.
        private void AppendLogSafe(string line)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) AppendLog(line);
            else dispatcher.BeginInvoke(() => AppendLog(line));
        }
    }
}
