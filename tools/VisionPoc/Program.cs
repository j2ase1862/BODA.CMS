using BODA.CMS.Analytics;
using BODA.CMS.Vision;

// P4 비전 진단 PoC — 시나리오:
//   사이클 0~119  : 정상 (마커 중심·지름이 노이즈 범위에서만 흔들림)
//   사이클 120~   : 반복정밀도 드리프트 주입 (+0.04px/사이클, x방향) — 기계적 마모/백래시 흉내
//   사이클 160~   : 마커 지름 감소 주입 (-0.06px/사이클) — 엔드이펙터 마모 흉내
// 로봇이 사이클마다 같은 자세로 정지했을 때 1장씩 촬영한다고 가정 (비개입 — 라인 카메라).

const int Cycles = 260;
const double BaseCx = 160.31, BaseCy = 120.77, BaseRadius = 14.0;
const int W = 320, H = 240;

var scene = new SyntheticMarkerScene(seed: 7) { NoiseAmplitude = 4.0 };
var jitter = new Random(11);
var monitor = new RepeatabilityMonitor("cell-A", new VisionOptions
{
    MmPerPixel = 0.05,       // 캘리브레이션: 1px = 0.05mm (FOV 16mm/320px 가정)
    BaselineCycles = 60,
});

int alerts = 0;
monitor.AlertRaised += a =>
{
    alerts++;
    Console.ForegroundColor = a.Severity switch
    {
        CbmSeverity.Alarm => ConsoleColor.Red,
        CbmSeverity.Warning => ConsoleColor.Yellow,
        _ => ConsoleColor.Green,
    };
    Console.WriteLine($"  ⚠ [{a.Severity}] {a.Message}");
    Console.ResetColor();
};

Console.WriteLine("=== P4 비전 진단 PoC — 반복정밀도 드리프트 + 마모 (합성 씬) ===");
Console.WriteLine($"해상도 {W}x{H}, 스케일 0.05mm/px, 기준선 60사이클, 드리프트@120, 마모@160\n");

int detected = 0;
double sumErr = 0, maxErr = 0;

for (int c = 0; c < Cycles; c++)
{
    // 진실값: 정지 반복 오차(±0.15px 지터) + 주입 결함
    double drift = c >= 120 ? (c - 120) * 0.04 : 0;
    double wear = c >= 160 ? (c - 160) * 0.06 : 0;
    double cx = BaseCx + drift + (jitter.NextDouble() - 0.5) * 0.3;
    double cy = BaseCy + (jitter.NextDouble() - 0.5) * 0.3;
    double r = BaseRadius - wear;

    GrayImage frame = scene.Render(W, H, cx, cy, r);
    MarkerObservation? obs = MarkerDetector.Detect(frame);
    if (obs is null)
    {
        Console.WriteLine($"[{c:D3}] 마커 미검출!");
        continue;
    }

    detected++;
    double err = Math.Sqrt(Math.Pow(obs.CxPx - cx, 2) + Math.Pow(obs.CyPx - cy, 2));
    sumErr += err;
    maxErr = Math.Max(maxErr, err);

    monitor.Ingest(obs);

    if (c % 40 == 0 || c is 120 or 160)
    {
        VisionSnapshot s = monitor.Snapshot;
        Console.WriteLine($"[{c:D3}] 중심=({obs.CxPx:0.00},{obs.CyPx:0.00})px 지름={obs.DiameterPx:0.0}px " +
                          $"| {s.Phase} 이탈={s.LastRadialMm:0.000}mm worstZ={s.WorstZ:0.0}");
    }
}

VisionSnapshot final = monitor.Snapshot;
Console.WriteLine($"\n=== 결과 ===");
Console.WriteLine($"검출률: {detected}/{Cycles}, 서브픽셀 오차 평균 {sumErr / detected:0.000}px / 최대 {maxErr:0.000}px");
Console.WriteLine($"최종 이탈: {final.LastRadialMm:0.000}mm, 지름: {final.LastDiameterMm:0.000}mm, 활성 알림: {final.ActiveAlertCount}, 알림 발화: {alerts}건");
