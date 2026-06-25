using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DoosanMonitor.Models;
using DoosanMonitor.Mvvm;
using DoosanMonitor.Services;
using DoosanMonitor.Services.Drfl;

namespace DoosanMonitor.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ModbusConnectionService _modbus = new();
        private readonly DrflMonitorService _drfl = new();
        private readonly ModbusTelemetryService _modbusTelemetry;
        private readonly StringBuilder _log = new();

        // DRFL 콜백은 ~100Hz로 들어오므로 UI는 이 간격으로만 갱신(throttle).
        private static readonly TimeSpan UiThrottle = TimeSpan.FromMilliseconds(100);
        private DateTime _lastUiUpdate = DateTime.MinValue;
        private const uint DrflPort = 12345; // DRFL 전용 포트(Modbus 502와 별개)

        private string _ipAddress = "192.168.137.100"; // 두산 기본 IP. 실제 컨트롤러 IP로 변경
        private string _port = "502";
        private string _statusMessage = "대기 중";
        private Brush _statusBrush = Brushes.Gray;
        private bool _isConnected;
        private string _logText = string.Empty;

        private string _drflStatus = "DRFL 대기 중";
        private Brush _drflBrush = Brushes.Gray;
        private bool _isMonitoring;
        private string _axisReadout = "(모니터링 시작 전)";

        private string _modbusMonStatus = "Modbus 모니터링 대기";
        private Brush _modbusMonBrush = Brushes.Gray;
        private bool _isModbusMonitoring;
        private string _modbusReadout = "(모니터링 시작 전)";

        public MainViewModel()
        {
            ConnectCommand = new AsyncRelayCommand(ToggleConnectionAsync);
            DrflCommand = new AsyncRelayCommand(ToggleMonitoringAsync);
            ModbusMonitorCommand = new AsyncRelayCommand(ToggleModbusMonitoringAsync);

            _drfl.SampleReceived += OnSampleReceived;
            _drfl.StateChanged += OnRobotStateChanged;
            _drfl.Disconnected += OnDrflDisconnected;

            _modbusTelemetry = new ModbusTelemetryService(_modbus, unitId: 1);
            _modbusTelemetry.SampleReceived += OnModbusSampleReceived;
            _modbusTelemetry.PollError += OnModbusPollError;
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

        // ── DRFL 패시브 모니터링 ──
        public string DrflStatus { get => _drflStatus; set => SetProperty(ref _drflStatus, value); }
        public Brush DrflBrush { get => _drflBrush; set => SetProperty(ref _drflBrush, value); }
        public string AxisReadout { get => _axisReadout; set => SetProperty(ref _axisReadout, value); }

        public bool IsMonitoring
        {
            get => _isMonitoring;
            set { if (SetProperty(ref _isMonitoring, value)) OnPropertyChanged(nameof(DrflButtonText)); }
        }

        public string DrflButtonText => IsMonitoring ? "모니터링 중지" : "DRFL 모니터링 시작";

        // ── Modbus 텔레메트리 (Phase 1 / Basic) ──
        public string ModbusMonStatus { get => _modbusMonStatus; set => SetProperty(ref _modbusMonStatus, value); }
        public Brush ModbusMonBrush { get => _modbusMonBrush; set => SetProperty(ref _modbusMonBrush, value); }
        public string ModbusReadout { get => _modbusReadout; set => SetProperty(ref _modbusReadout, value); }

        public bool IsModbusMonitoring
        {
            get => _isModbusMonitoring;
            set { if (SetProperty(ref _isModbusMonitoring, value)) OnPropertyChanged(nameof(ModbusMonButtonText)); }
        }

        public string ModbusMonButtonText => IsModbusMonitoring ? "Modbus 모니터링 중지" : "Modbus 모니터링 시작";

        public AsyncRelayCommand ConnectCommand { get; }
        public AsyncRelayCommand DrflCommand { get; }
        public AsyncRelayCommand ModbusMonitorCommand { get; }

        private async Task ToggleConnectionAsync()
        {
            // 이미 연결돼 있으면 해제
            if (IsConnected)
            {
                // 폴링 중이면 먼저 중지(닫힌 소켓 폴링 방지).
                if (IsModbusMonitoring)
                {
                    _modbusTelemetry.Stop();
                    IsModbusMonitoring = false;
                    ModbusMonStatus = "Modbus 모니터링 중지";
                    ModbusMonBrush = Brushes.Gray;
                }
                _modbus.Disconnect();
                IsConnected = false;
                SetStatus("연결 해제됨", Brushes.Gray);
                AppendLog("연결을 해제했습니다.");
                return;
            }

            // 입력 검증
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

            // 연결 시도
            try
            {
                SetStatus("연결 중...", Brushes.DarkOrange);
                AppendLog($"연결 시도: {IpAddress.Trim()}:{port}");

                await _modbus.ConnectAsync(IpAddress.Trim(), port);

                IsConnected = true;
                SetStatus("연결 성공", Brushes.SeaGreen);
                AppendLog("TCP 연결 성공.");

                // 시험 읽기(선택): 홀딩 레지스터 40001~ 4개. 실패해도 연결은 정상일 수 있음.
                try
                {
                    ushort[] regs = await _modbus.TryReadHoldingAsync(unitId: 1, start: 0, count: 4);
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

        // ── DRFL: 비개입 패시브 모니터링 토글 ──
        private async Task ToggleMonitoringAsync()
        {
            if (IsMonitoring)
            {
                _drfl.Disconnect();
                IsMonitoring = false;
                DrflStatus = "DRFL 모니터링 중지";
                DrflBrush = Brushes.Gray;
                AppendLog("DRFL 모니터링을 중지했습니다.");
                return;
            }

            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                DrflStatus = "IP 주소를 입력하세요";
                DrflBrush = Brushes.OrangeRed;
                return;
            }

            string ip = IpAddress.Trim();
            try
            {
                DrflStatus = "DRFL 연결 중...";
                DrflBrush = Brushes.DarkOrange;
                AppendLog($"DRFL 연결 시도: {ip}:{DrflPort} (패시브 모니터링)");

                // 네이티브 호출은 잠시 블로킹될 수 있으므로 백그라운드에서.
                await Task.Run(() => _drfl.Connect(ip, DrflPort));

                IsMonitoring = true;
                DrflStatus = "DRFL 모니터링 중";
                DrflBrush = Brushes.SeaGreen;
                AppendLog("DRFL 연결 성공. 콜백 수신 시작(명령 권한 미취득 — 비개입).");

                try
                {
                    SYSTEM_VERSION v = _drfl.GetSystemVersion();
                    AppendLog($"컨트롤러={v._szController}, 모델={v._szRobotModel}, S/N={v._szRobotSerial}");
                }
                catch (Exception vex)
                {
                    AppendLog("시스템 버전 조회 생략: " + vex.GetBaseException().Message);
                }
            }
            catch (DllNotFoundException)
            {
                IsMonitoring = false;
                DrflStatus = "DRFL DLL 없음";
                DrflBrush = Brushes.Firebrick;
                AppendLog("DRFLWin64.dll 을 찾을 수 없습니다. exe 폴더에 DLL + Poco 의존 DLL 을 두세요.");
            }
            catch (Exception ex)
            {
                IsMonitoring = false;
                DrflStatus = "DRFL 연결 실패";
                DrflBrush = Brushes.Firebrick;
                AppendLog("DRFL 연결 실패: " + ex.GetBaseException().Message);
            }
        }

        // ⚠️ 네이티브 스레드에서 호출됨 → UI 스레드로 마샬링 + throttle.
        private void OnSampleReceived(MonitoringSample s)
        {
            DateTime now = DateTime.Now;
            if (now - _lastUiUpdate < UiThrottle) return; // 100Hz → ~10Hz로 솎음
            _lastUiUpdate = now;

            string text = BuildAxisReadout(s);
            Application.Current?.Dispatcher.BeginInvoke(() => AxisReadout = text);
        }

        private void OnRobotStateChanged(ROBOT_STATE state) =>
            Application.Current?.Dispatcher.BeginInvoke(() => AppendLog($"로봇 상태: {state}"));

        private void OnDrflDisconnected() =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsMonitoring = false;
                DrflStatus = "DRFL 연결 끊김";
                DrflBrush = Brushes.Firebrick;
                AppendLog("DRFL 연결이 끊겼습니다.");
            });

        private static string BuildAxisReadout(MonitoringSample s)
        {
            string Row(string label, float[] v, string fmt) =>
                $"{label,-8}" + string.Join(" ", v.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(9)));

            var sb = new StringBuilder();
            sb.AppendLine($"축          J1        J2        J3        J4        J5        J6");
            sb.AppendLine(Row("위치°",   s.JointPosition, "0.00"));
            sb.AppendLine(Row("토크",    s.JointTorqueSensor, "0.00"));
            sb.AppendLine(Row("전류A",   s.MotorCurrent, "0.00"));
            sb.AppendLine(Row("온도℃",   s.MotorTemperature, "0.0"));
            return sb.ToString();
        }

        // ── Modbus 텔레메트리 폴링 토글 (기존 _modbus 연결 재사용) ──
        private async Task ToggleModbusMonitoringAsync()
        {
            if (IsModbusMonitoring)
            {
                _modbusTelemetry.Stop();
                IsModbusMonitoring = false;
                ModbusMonStatus = "Modbus 모니터링 중지";
                ModbusMonBrush = Brushes.Gray;
                AppendLog("Modbus 모니터링을 중지했습니다.");
                return;
            }

            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                ModbusMonStatus = "IP 주소를 입력하세요";
                ModbusMonBrush = Brushes.OrangeRed;
                return;
            }
            if (!int.TryParse(Port, out int port) || port < 1 || port > 65535)
            {
                ModbusMonStatus = "포트 값이 올바르지 않습니다";
                ModbusMonBrush = Brushes.OrangeRed;
                return;
            }

            try
            {
                // 연결이 없으면 먼저 연결(단일 세션 재사용).
                if (!_modbus.IsConnected)
                {
                    ModbusMonStatus = "연결 중...";
                    ModbusMonBrush = Brushes.DarkOrange;
                    AppendLog($"Modbus 연결 시도: {IpAddress.Trim()}:{port}");
                    await _modbus.ConnectAsync(IpAddress.Trim(), port);
                    IsConnected = true;
                    SetStatus("연결 성공", Brushes.SeaGreen);
                }

                _modbusTelemetry.Start(intervalMs: 100); // 10Hz
                IsModbusMonitoring = true;
                ModbusMonStatus = "Modbus 모니터링 중";
                ModbusMonBrush = Brushes.SeaGreen;
                AppendLog("Modbus 폴링 시작 (위치 270~275·온도 300~305·전류 400~405, unit=1).");
            }
            catch (Exception ex)
            {
                IsModbusMonitoring = false;
                ModbusMonStatus = "Modbus 연결 실패";
                ModbusMonBrush = Brushes.Firebrick;
                AppendLog("Modbus 모니터링 시작 실패: " + ex.GetBaseException().Message);
            }
        }

        // ⚠️ 폴링 스레드에서 호출 → UI 스레드로 마샬링. (폴링이 10Hz라 별도 throttle 불필요.)
        private void OnModbusSampleReceived(ModbusTelemetrySample s)
        {
            string text = BuildModbusReadout(s);
            Application.Current?.Dispatcher.BeginInvoke(() => ModbusReadout = text);
        }

        private void OnModbusPollError(string message) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ModbusMonStatus = "읽기 오류";
                ModbusMonBrush = Brushes.OrangeRed;
            });

        private static string BuildModbusReadout(ModbusTelemetrySample s)
        {
            string Row(string label, System.Collections.Generic.IEnumerable<float> v, string fmt) =>
                $"{label,-8}" + string.Join(" ", v.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(9)));

            var sb = new StringBuilder();
            sb.AppendLine("축          J1        J2        J3        J4        J5        J6");
            sb.AppendLine(Row("위치°",   s.JointPositionDeg,                 "0.0"));
            sb.AppendLine(Row("온도℃",   s.Temperature.Select(t => (float)t), "0"));
            sb.AppendLine(Row("전류*",   s.CurrentTorqueRaw.Select(c => (float)c), "0"));
            return sb.ToString();
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
    }
}
