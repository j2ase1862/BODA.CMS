using BODA.CMS.Collector;
using BODA.CMS.Collector.Storage;
using BODA.CMS.Comms;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.Drivers.Doosan;
using BODA.CMS.Drivers.Simulated;
using Microsoft.Extensions.Options;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));

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
builder.Services.AddSingleton(new VendorDescriptor("sim", "시뮬레이터", () => new IRobotTelemetrySource[]
{
    new SimulatedRobotSource("basic", "가상 Basic (범용 모사)", rateHz: 10, deep: false),
    new SimulatedRobotSource("pro", "가상 Pro (네이티브 모사)", rateHz: 100, deep: true),
}));

// ── 파이프라인: 펌프 → 버퍼 → 배치 적재 ──
builder.Services.AddSingleton<FrameBuffer>();
builder.Services.AddSingleton<IFrameStore>(sp =>
    sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Storage.Enabled
        ? ActivatorUtilities.CreateInstance<TimescaleFrameStore>(sp)
        : ActivatorUtilities.CreateInstance<LogFrameStore>(sp));
builder.Services.AddHostedService<CollectorService>();
builder.Services.AddHostedService<StorageWorker>();

await builder.Build().RunAsync();
