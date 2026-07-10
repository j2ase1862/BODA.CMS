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
// CollectorService 는 REST(/api/robots)가 재구성을 호출해야 하므로 싱글턴으로도 노출.
builder.Services.AddSingleton<CollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorService>());
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

// 알림 이력: DB(telemetry_alerts)가 있으면 전체 이력에서 필터·페이지 조회,
// 없거나(dry-run) 미기동이면 메모리 링(최근 200)으로 폴백. severity 는 쉼표 목록.
app.MapGet("/api/alerts", async (DashboardState dashboard, IFrameStore store, ILogger<Program> logger,
    string? robot, string? channel, string? severity, DateTime? before, int? take, CancellationToken ct) =>
{
    var query = new AlertQuery(
        string.IsNullOrWhiteSpace(robot) ? null : robot,
        string.IsNullOrWhiteSpace(channel) ? null : channel,
        string.IsNullOrWhiteSpace(severity) ? null
            : severity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        before?.ToUniversalTime(),
        Math.Clamp(take ?? 50, 1, 200));
    try
    {
        if (await store.QueryAlertsAsync(query, ct) is { } history)
            return Results.Json(history);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "알림 이력 DB 조회 실패 — 메모리 링(최근 200)으로 폴백.");
    }
    return Results.Json(dashboard.GetAlerts(query));
});

// ── 로봇 구성 조회/교체 — WPF 앱의 제조사 전환이 대시보드에도 반영되도록 (내부망 전용 API) ──
app.MapGet("/api/robots", (CollectorService collector) => Results.Json(collector.CurrentRobots));

app.MapPut("/api/robots", async (List<RobotOptions> robots, CollectorService collector,
    ILogger<Program> logger, CancellationToken ct) =>
{
    foreach (RobotOptions r in robots)
    {
        if (string.IsNullOrWhiteSpace(r.RobotId) || string.IsNullOrWhiteSpace(r.Vendor) || string.IsNullOrWhiteSpace(r.Host))
            return Results.BadRequest(new { error = "robotId/vendor/host 는 필수입니다." });
        if (!collector.KnownVendors.Contains(r.Vendor, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"미등록 벤더 '{r.Vendor}' — 사용 가능: {string.Join(", ", collector.KnownVendors)}" });
    }
    if (robots.Select(r => r.RobotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != robots.Count)
        return Results.BadRequest(new { error = "RobotId 가 중복됩니다." });

    int pumps = await collector.ReconfigureAsync(robots, ct);

    // 재시작 후에도 유지되도록 appsettings.json 에 영속화 — 실패해도 런타임 적용은 유효.
    try { PersistRobots(robots); }
    catch (Exception ex) { logger.LogError(ex, "로봇 구성 영속화 실패 — 재시작하면 이전 구성으로 돌아갑니다."); }

    return Results.Json(new { robots = robots.Count, pumps });
});

await app.RunAsync();

// 로봇 목록만 갈아끼우고 나머지(Urls·Storage·로깅)는 그대로 보존한다.
static void PersistRobots(List<RobotOptions> robots)
{
    string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
        ?? new System.Text.Json.Nodes.JsonObject();
    if (root["Collector"] is not System.Text.Json.Nodes.JsonObject collector)
        root["Collector"] = collector = new System.Text.Json.Nodes.JsonObject();

    var list = new System.Text.Json.Nodes.JsonArray();
    foreach (RobotOptions r in robots)
    {
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["RobotId"] = r.RobotId,
            ["Vendor"] = r.Vendor,
            ["Host"] = r.Host,
        };
        if (r.Port is int port) o["Port"] = port;
        if (r.Channels is { Count: > 0 }) o["Channels"] = new System.Text.Json.Nodes.JsonArray(
            r.Channels.Select(c => (System.Text.Json.Nodes.JsonNode)c).ToArray());
        list.Add(o);
    }
    collector["Robots"] = list;

    File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글 RobotId 를 이스케이프 없이
    }));
}
