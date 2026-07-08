using BODA.CMS.Collector;
using BODA.CMS.Collector.Dashboard;
using BODA.CMS.Collector.Storage;
using BODA.CMS.Comms;
using BODA.CMS.Core.Licensing;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan;
using BODA.CMS.Drivers.Jaka;
using BODA.CMS.Drivers.Simulated;
using Microsoft.Extensions.Options;

// Windows 서비스로 실행되면 작업 디렉터리가 System32라 appsettings.json 을 못 찾는다 —
// 콘텐츠 루트를 exe 위치로 고정 (콘솔 실행에도 무해).
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Windows 서비스 지원 (P5): 실제 서비스로 실행될 때만 배선 — 콘솔 실행은 완전히 기존과 동일
// (무조건 호출하면 콘솔 모드에도 EventLog 로거가 끼어든다). 등록: tools/install-service.ps1.
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddWindowsService(options => options.ServiceName = "BODA.CMS.Collector");

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));

// ── 라이선스 (P5): exe 옆 license.json — 없으면 평가판, 불량/만료면 Basic 강등 ──
builder.Services.AddSingleton(LicenseVerifier.Load(Path.Combine(AppContext.BaseDirectory, "license.json")));

// ── 벤더 카탈로그 (컴포지션 루트 — 벤더 타입은 여기서만 등장, ROADMAP §3 벤더 격리) ──
// 새 벤더 지원 = Drivers.{Vendor} 구현 + 아래 한 항목.
builder.Services.AddSingleton(new VendorDescriptor("doosan", "두산로보틱스", () =>
{
    var modbus = new ModbusConnectionService(); // 수집기에서는 드라이버가 세션을 소유(재연결 시 새 소켓)
    return new IRobotTelemetrySource[]
    {
        new DoosanModbusSource(modbus, ownsConnection: true), // 범용 채널 → Basic
        new DoosanDrflSource(),                               // 네이티브 채널 → Pro
    };
}));
builder.Services.AddSingleton(new VendorDescriptor("jaka", "JAKA", () => new IRobotTelemetrySource[]
{
    new JakaJsonSource(), // 네이티브 모니터 스트림(수신 전용) — 실기 확정 전 Basic. Modbus 채널은 §5.2 잔여.
}));
builder.Services.AddSingleton(new VendorDescriptor("sim", "시뮬레이터", () => new IRobotTelemetrySource[]
{
    new SimulatedRobotSource("basic", "가상 Basic (범용 모사)", rateHz: 10, deep: false),
    new SimulatedRobotSource("pro", "가상 Pro (네이티브 모사)", rateHz: 100, deep: true),
}));

// ── 파이프라인: 펌프 → 버퍼 → 배치 적재 + 무인 감시(CBM/ML) + 대시보드 ──
builder.Services.AddSingleton<FrameBuffer>();
builder.Services.AddSingleton<DashboardState>();
builder.Services.AddSingleton<IFrameStore>(sp =>
    sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Storage.Enabled
        ? ActivatorUtilities.CreateInstance<TimescaleFrameStore>(sp)
        : ActivatorUtilities.CreateInstance<LogFrameStore>(sp));
builder.Services.AddHostedService<CollectorService>();
builder.Services.AddHostedService<StorageWorker>();

WebApplication app = builder.Build();

// ── 웹 대시보드 (P5): 정적 페이지(wwwroot) + REST — 원격/다중 사용자 모니터링 ──
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (DashboardState dashboard, LicenseStatus license) => Results.Json(new
{
    license = new { mode = license.Mode.ToString(), description = license.Description },
    serverTimeUtc = DateTime.UtcNow,
    channels = dashboard.GetStatus(),
}));

app.MapGet("/api/alerts", (DashboardState dashboard, int? take) =>
    Results.Json(dashboard.GetAlerts(Math.Clamp(take ?? 50, 1, 200))));

await app.RunAsync();
