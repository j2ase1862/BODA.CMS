using System;
using System.IO;
using System.Windows;
using BODA.CMS.Core.Licensing;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan;
using BODA.CMS.Drivers.Jaka;
using BODA.CMS.Drivers.Simulated;
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

            DataContext = new MainViewModel(modbus, vendors, license);
        }
    }
}
