using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BODA.CMS.Core.Licensing;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan;
using BODA.CMS.Drivers.Jaka;
using BODA.CMS.Drivers.Simulated;
using BODA.CMS.Drivers.UR;
using BODA.CMS.Comms;
using BODA.CMS.ViewModels;

namespace BODA.CMS
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 컴포지션 루트 — 벤더 드라이버 타입은 여기서만 등장한다(ROADMAP §3 벤더 격리).
            // 새 벤더 지원 = 드라이버 모듈 구현 후 이 카탈로그에 항목 추가뿐.
            // (P1에서 구성 파일 기반 다중 로봇 로딩으로 대체 예정.)
            var modbus = new ModbusConnectionService();
            var vendors = new[]
            {
                new VendorDescriptor("doosan", "두산로보틱스", () => new IRobotTelemetrySource[]
                {
                    new DoosanModbusSource(modbus),   // 범용 채널 → Basic
                    new DoosanDrflSource(),           // 네이티브 채널 → Pro
                }),
                new VendorDescriptor("jaka", "JAKA", () => new IRobotTelemetrySource[]
                {
                    new JakaJsonSource(),    // 네이티브 모니터 스트림(수신 전용) → 실기 확정 전 Basic
                    // Modbus 채널: 실기 프로브로 레지스터 맵 검증 후 추가 (§5.2)
                }),
                new VendorDescriptor("ur", "유니버설로봇 (UR)", () => new IRobotTelemetrySource[]
                {
                    new UrRtdeSource(), // RTDE 출력 구독(수신 전용) → Pro. 관절 토크센서 미탑재 — 전류·온도·모델토크
                }),
                // Rokae: Drivers.Rokae 구현 후 여기에 등록 (ROADMAP §5.3)
                new VendorDescriptor("sim", "시뮬레이터 (가상 데이터)", () => new IRobotTelemetrySource[]
                {
                    new SimulatedRobotSource("basic", "가상 Basic (범용 모사)", rateHz: 10, deep: false),
                    // Pro 채널은 t=100s에 J3 결함 주입 — CBM(기준선 60s 학습 → 알람) 데모/검증용.
                    new SimulatedRobotSource("pro", "가상 Pro (네이티브 모사)", rateHz: 100, deep: true, faultStartSeconds: 100),
                }),
            };
            // 라이선스 (P5): exe 옆 license.json — 없으면 평가판, 불량/만료면 Basic 강등.
            LicenseStatus license = LicenseVerifier.Load(Path.Combine(AppContext.BaseDirectory, "license.json"));

            // 제조사/IP 선택을 감시 서버(웹 대시보드)에도 자동 반영 — 서버가 없으면 로그만 남기고 무시.
            _collectorSync = new Services.CollectorSync();
            DataContext = new MainViewModel(modbus, vendors, license, _collectorSync);
        }

        // 웹 대시보드 열기 — 주소 규칙은 CollectorSync 와 동일(BODA_COLLECTOR_URL → 기본 localhost:5100).
        private readonly Services.CollectorSync _collectorSync;

        private void OnDashboardClick(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_collectorSync.BaseUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("브라우저 열기 실패: " + ex.GetBaseException().Message,
                    "웹 대시보드", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // AI 재학습 창 — 단일 인스턴스 (열려 있으면 앞으로만).
        private Views.RetrainWindow? _retrainWindow;

        private void OnRetrainClick(object sender, RoutedEventArgs e)
        {
            if (_retrainWindow is { IsLoaded: true })
            {
                _retrainWindow.Activate();
                return;
            }
            var vm = (MainViewModel)DataContext;
            _retrainWindow = new Views.RetrainWindow(new RetrainViewModel(vm.ReloadMlModels)) { Owner = this };
            _retrainWindow.Closed += (_, _) => _retrainWindow = null;
            _retrainWindow.Show();
        }

        // 다크 테마(Themes/Theme.xaml)에 맞춰 OS 타이틀바도 어둡게 — Win10 1809+/Win11.
        // 미지원 OS 에서는 조용히 무시된다(밝은 타이틀바로 동작).
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            int on = 1;
            _ = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));

            // 기본 크기(1220×1020)가 화면 작업 영역보다 크면 맞춰 줄인다 (노트북·저해상도 현장 PC).
            if (Height > SystemParameters.WorkArea.Height) Height = SystemParameters.WorkArea.Height - 8;
            if (Width > SystemParameters.WorkArea.Width) Width = SystemParameters.WorkArea.Width - 8;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
