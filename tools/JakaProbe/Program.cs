using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BODA.CMS.Drivers.Jaka;

// JAKA 실기 프로브 — 모두 수신 전용/패시브. 명령 포트(10001)에 아무것도 보내지 않는다.
//   인자: [ip] [port] [seconds]  (기본 192.168.1.100 / 10000 / 5)
// 목적(§6 4단계 준비):
//   1) 모니터 스트림 수신 확인 + 패킷 주기 실측
//   2) 패킷의 전체 키 + 숫자 배열 필드(신호 후보) 열거 → 드라이버 매핑·capability 확정 근거
//   3) joint_actual_position 단위 대조(펜던트 표시값과 비교 — 도° 여부 확인)

string ip = args.Length > 0 ? args[0] : "192.168.1.100";
int port = args.Length > 1 ? int.Parse(args[1]) : 10000;
int seconds = args.Length > 2 ? int.Parse(args[2]) : 5;

Console.WriteLine($"=== JAKA 모니터 스트림 프로브 @ {ip}:{port} ({seconds}초 수신) ===\n");

using var tcp = new TcpClient();
try
{
    if (!tcp.ConnectAsync(ip, port).Wait(3000)) { Console.WriteLine("연결 타임아웃"); return; }
}
catch (Exception ex) { Console.WriteLine("연결 실패: " + ex.GetBaseException().Message); return; }
Console.WriteLine("TCP 연결 OK — 수신 대기…\n");

var framer = new JakaStreamFramer();
var stream = tcp.GetStream();
stream.ReadTimeout = 2000;
var buffer = new byte[8192];
var packetTimes = new List<DateTime>();
string? lastJson = null;

DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
while (DateTime.UtcNow < deadline)
{
    int n;
    try { n = stream.Read(buffer, 0, buffer.Length); }
    catch (IOException) { Console.WriteLine("(2초 무수신)"); continue; }
    if (n == 0) { Console.WriteLine("원격 종료"); break; }

    foreach (string json in framer.Append(Encoding.UTF8.GetString(buffer, 0, n)))
    {
        packetTimes.Add(DateTime.UtcNow);
        lastJson = json;
    }
}

Console.WriteLine($"수신 패킷: {packetTimes.Count}개 (≈{packetTimes.Count / (double)seconds:0.0} Hz)");
if (lastJson is null) { Console.WriteLine("⚠️ 패킷 미수신 — 포트/펌웨어 확인."); return; }

Console.WriteLine("\n― 마지막 패킷 필드 분석 (숫자 배열 = 신호 후보) ―");
using JsonDocument doc = JsonDocument.Parse(lastJson);
JsonElement root = doc.RootElement;
if (root.TryGetProperty("data", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object)
{
    Console.WriteLine("(래핑 감지: data 오브젝트 내부 분석)");
    root = inner;
}
foreach (JsonProperty p in root.EnumerateObject())
{
    if (p.Value.ValueKind == JsonValueKind.Array &&
        p.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number))
    {
        double[] v = p.Value.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        string marker = v.Length == 6 ? " ★축별 후보" : "";
        Console.WriteLine($"  {p.Name,-28} [{string.Join(", ", v.Select(x => x.ToString("0.###")))}]{marker}");
    }
    else
    {
        Console.WriteLine($"  {p.Name,-28} ({p.Value.ValueKind})");
    }
}

Console.WriteLine("\n― 드라이버 파서 통과 확인 ―");
var frame = JakaPacketParser.Parse(lastJson, DateTime.UtcNow);
Console.WriteLine(frame is null
    ? "⚠️ 파서가 프레임을 만들지 못함 — joint_actual_position 키 확인 필요."
    : $"OK — 위치 [{string.Join(", ", frame.JointPositionDeg.Select(x => x.ToString("0.00")))}]° " +
      $"(펜던트 표시값과 대조!), 원시 신호 {frame.VendorRaw?.Count ?? 0}종: " +
      string.Join(", ", frame.VendorRaw?.Keys ?? Enumerable.Empty<string>()));
