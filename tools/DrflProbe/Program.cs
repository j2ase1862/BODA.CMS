using System;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using BODA.CMS.Drivers.Doosan;
using BODA.CMS.Drivers.Doosan.Drfl;
using NModbus;

// 실기 검증 프로브 — 모두 읽기 전용/패시브. 로봇 명령 권한을 취득하지 않는다.
//   인자: [ip] [modbusPort] [drflPort]  (기본 192.168.1.100 / 502 / 12345)
string ip = args.Length > 0 ? args[0] : "192.168.1.100";
int modbusPort = args.Length > 1 ? int.Parse(args[1]) : 502;
uint drflPort = args.Length > 2 ? uint.Parse(args[2]) : 12345;

Console.WriteLine($"=== 두산 로봇 실기 프로브 @ {ip} (Modbus {modbusPort}, DRFL {drflPort}) ===\n");

ProbeModbus(ip, modbusPort);
Console.WriteLine();
ProbeDrfl(ip, drflPort);

Console.WriteLine("\n=== 완료 ===");


static void ProbeModbus(string ip, int port)
{
    Console.WriteLine($"[Modbus] {ip}:{port} 연결…");
    try
    {
        using var tcp = new TcpClient();
        if (!tcp.ConnectAsync(ip, port).Wait(3000)) { Console.WriteLine("  연결 타임아웃"); return; }
        var master = new ModbusFactory().CreateMaster(tcp);
        master.Transport.ReadTimeout = 1000;
        Console.WriteLine("  TCP 연결 OK. UnitId 1/0/255 × FC03(홀딩)/FC04(입력) 0~9 시도:");

        foreach (byte unit in new byte[] { 1, 0, 255 })
        {
            TryRead($"    unit={unit} FC03(40001~)", () => master.ReadHoldingRegisters(unit, 0, 10));
            TryRead($"    unit={unit} FC04(30001~)", () => master.ReadInputRegisters(unit, 0, 10));
        }
    }
    catch (Exception ex) { Console.WriteLine("  실패: " + ex.GetBaseException().Message); }
}

static void TryRead(string label, Func<ushort[]> read)
{
    try { Console.WriteLine($"{label}: " + string.Join(" ", read().Select(r => r.ToString("D5")))); }
    catch (Exception ex) { Console.WriteLine($"{label}: ✗ {ex.GetBaseException().Message}"); }
}

static void ProbeDrfl(string ip, uint port)
{
    // DLL 자체 라이브러리 버전(연결 불필요) — 컨트롤러 v2.11 과의 프로토콜 정합 진단용.
    try
    {
        var h = DrflInterop.CreateRobotControl();
        if (h != IntPtr.Zero)
        {
            var pv = DrflInterop.GetLibraryVersion(h);
            string lib = pv == IntPtr.Zero ? "(null)" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(pv) ?? "(null)";
            Console.WriteLine($"[DRFL] DLL 라이브러리 버전: {lib}");
            DrflInterop.DestroyRobotControl(h);
        }
    }
    catch (Exception ex) { Console.WriteLine("[DRFL] 라이브러리 버전 조회 실패: " + ex.GetBaseException().Message); }

    Console.WriteLine($"[DRFL] {ip}:{port} 패시브 모니터링 (3초간 수신)…");
    using var svc = new DrflMonitorService();

    int frames = 0;
    MonitoringSample? last = null;
    svc.SampleReceived += s => { Interlocked.Increment(ref frames); Volatile.Write(ref last, s); };
    svc.StateChanged += st => Console.WriteLine($"  [상태] {st}");
    svc.Disconnected += () => Console.WriteLine("  [연결 끊김]");

    try
    {
        svc.Connect(ip, port);
        Console.WriteLine("  연결 OK (명령 권한 미취득 — 비개입).");

        try
        {
            var v = svc.GetSystemVersion();
            Console.WriteLine($"  컨트롤러={v._szController}  모델={v._szRobotModel}  S/N={v._szRobotSerial}");
            Console.WriteLine($"  → DRCF V2.11.1(GV02110100) 기대값과 컨트롤러 버전 문자열 대조 지점");
        }
        catch (Exception ex) { Console.WriteLine("  버전 조회 생략: " + ex.GetBaseException().Message); }

        Thread.Sleep(3000);

        var s = Volatile.Read(ref last);
        Console.WriteLine($"  수신 프레임 수: {frames} (≈{frames / 3.0:0} Hz)");
        if (s is null) { Console.WriteLine("  ⚠️ 콜백 미수신 — 컨트롤러가 모니터링 스트림을 안 보냈거나 구조체 정합 문제."); }
        else
        {
            Console.WriteLine($"  최신 프레임(축 {s.AxisCount}개, syncTime={s.SyncTime:0.000}):");
            PrintAxis("위치°", s.JointPosition, "0.00");
            PrintAxis("토크 ", s.JointTorqueSensor, "0.00");
            PrintAxis("전류A", s.MotorCurrent, "0.00");
            PrintAxis("온도℃", s.MotorTemperature, "0.0");
        }
    }
    catch (DllNotFoundException) { Console.WriteLine("  ✗ DRFLWin64.dll 로드 실패(출력 폴더의 DLL/Poco 확인)."); }
    catch (Exception ex) { Console.WriteLine("  ✗ 실패: " + ex.GetBaseException().Message); }
}

static void PrintAxis(string label, float[] v, string fmt) =>
    Console.WriteLine($"    {label}  " + string.Join(" ", v.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(9))));
