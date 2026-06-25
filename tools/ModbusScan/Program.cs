using System.Net.Sockets;
using NModbus;

// Doosan CS Modbus 홀딩 레지스터(FC03) 스캐너 — 읽기 전용.
//   인자: [ip] [start] [count] [label]
//   매 실행마다: 비영 레지스터 출력 + 직전 스냅샷과 diff + 스냅샷 저장.
//   사용법: 정지 상태로 1회(baseline) → 한 축 조그 후 정지 → 다시 실행(변화 확인).
string ip = args.Length > 0 ? args[0] : "192.168.1.100";
int start = args.Length > 1 ? int.Parse(args[1]) : 0;
int count = args.Length > 2 ? int.Parse(args[2]) : 512;
string label = args.Length > 3 ? args[3] : "";
const byte unit = 1;

string snapPath = Path.Combine(AppContext.BaseDirectory, "snapshot.txt");

Console.WriteLine($"=== Modbus 스캔 @ {ip}:502 unit={unit} FC03 [{start}..{start + count - 1}] {label} ===");

var values = new SortedDictionary<int, ushort>();
using var tcp = new TcpClient();
if (!tcp.ConnectAsync(ip, 502).Wait(3000)) { Console.WriteLine("TCP 연결 타임아웃"); return; }
var master = new ModbusFactory().CreateMaster(tcp);
master.Transport.ReadTimeout = 1500;

int addr = start, end = start + count;
while (addr < end)
{
    int n = Math.Min(120, end - addr);
    try
    {
        var regs = master.ReadHoldingRegisters(unit, (ushort)addr, (ushort)n);
        for (int i = 0; i < regs.Length; i++) values[addr + i] = regs[i];
        addr += n;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  @{addr}+{n} 읽기 중단: {ex.GetBaseException().Message} (유효 범위 끝으로 추정)");
        break;
    }
}
int maxAddr = values.Count > 0 ? values.Keys.Max() : start;
Console.WriteLine($"읽은 레지스터 수: {values.Count}  (유효 끝 ~{maxAddr})\n");

// 직전 스냅샷 로드
var prev = new Dictionary<int, ushort>();
if (File.Exists(snapPath))
    foreach (var line in File.ReadAllLines(snapPath))
    {
        var p = line.Split(' ');
        if (p.Length == 2 && int.TryParse(p[0], out var a) && ushort.TryParse(p[1], out var v)) prev[a] = v;
    }

// 비영 레지스터
Console.WriteLine("[비영 레지스터]  addr(4xxxx) :   u16    (i16)");
foreach (var kv in values)
    if (kv.Value != 0)
        Console.WriteLine($"  {kv.Key,4} (4{kv.Key + 1:0000}) : {kv.Value,6}  ({(short)kv.Value,7})");

// 직전 대비 변화 — 조그한 축의 레지스터를 여기서 식별
if (prev.Count > 0)
{
    Console.WriteLine("\n[변화: 직전 스냅샷 대비]  addr(4xxxx) : 이전 → 현재   (Δi16)");
    bool any = false;
    foreach (var kv in values)
        if (prev.TryGetValue(kv.Key, out var old) && old != kv.Value)
        {
            any = true;
            Console.WriteLine($"  {kv.Key,4} (4{kv.Key + 1:0000}) : {old} → {kv.Value}   (Δ {(short)kv.Value - (short)old})");
        }
    if (!any) Console.WriteLine("  (변화 없음)");
}

File.WriteAllLines(snapPath, values.Select(kv => $"{kv.Key} {kv.Value}"));
Console.WriteLine($"\n스냅샷 저장됨 → 다음 실행 시 이 상태와 비교합니다.");
