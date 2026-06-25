using System;
using System.Runtime.InteropServices;

namespace DoosanMonitor.Services.Drfl
{
    /// <summary>ROBOT_STATE (DRFC.h). _GetRobotState 반환값.</summary>
    public enum ROBOT_STATE
    {
        STATE_INITIALIZING = 0,
        STATE_STANDBY,
        STATE_MOVING,
        STATE_SAFE_OFF,
        STATE_TEACHING,
        STATE_SAFE_STOP,
        STATE_EMERGENCY_STOP,
        STATE_HOMMING,
        STATE_RECOVERY,
        STATE_SAFE_STOP2,
        STATE_SAFE_OFF2,
        STATE_RESERVED1,
        STATE_RESERVED2,
        STATE_RESERVED3,
        STATE_RESERVED4,
        STATE_NOT_READY = 15,
        STATE_LAST,
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DRFL 콜백 시그니처. DRFL.h 의 함수 포인터 typedef 를 그대로 미러링.
    // ⚠️ DRFL_API 는 호출규약을 명시하지 않으므로 C 함수의 기본값 __cdecl 이다.
    //    DllImport / 델리게이트 모두 Cdecl 로 맞춰야 스택이 깨지지 않는다.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>TOnMonitoringDataCB: void(*)(const LPMONITORING_DATA). 인자는 구조체 포인터.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TOnMonitoringDataCB(IntPtr pData);

    /// <summary>TOnMonitoringStateCB: void(*)(const ROBOT_STATE).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TOnMonitoringStateCB(ROBOT_STATE eState);

    /// <summary>TOnDisconnectedCB: void(*)().</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TOnDisconnectedCB();

    /// <summary>
    /// DRFLWin64.dll 의 flat C API (extern "C", __cdecl) P/Invoke 바인딩.
    ///
    /// 배포 시 주의: 빌드 산출물 폴더(exe 옆)에 다음이 모두 있어야 로드된다.
    ///   DRFLWin64.dll  (DoosanRobotics/API-DRFL · library/Windows/64bits)
    ///   + PocoFoundation / PocoNet 등 동일 폴더의 의존 DLL
    /// 32비트로 빌드할 경우 DRFLWin32.dll 로 교체하고 PlatformTarget 을 x86 으로.
    /// </summary>
    internal static class DrflInterop
    {
        private const string Dll = "DRFLWin64.dll";

        // ── 인스턴스 ──
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_CreateRobotControl")]
        public static extern IntPtr CreateRobotControl();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_DestroyRobotControl")]
        public static extern void DestroyRobotControl(IntPtr pCtrl);

        // ── 연결 ──
        // C++ bool 은 1바이트 → 반드시 I1 로 마샬링(기본 4바이트 BOOL 로 두면 값이 깨진다).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_OpenConnection", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool OpenConnection(IntPtr pCtrl, string lpszIpAddr, uint usPort);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_CloseConnection")]
        public static extern void CloseConnection(IntPtr pCtrl);

        // ── 속성 ──
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_GetSystemVersion")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetSystemVersion(IntPtr pCtrl, ref SYSTEM_VERSION pVersion);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_GetLibraryVersion")]
        public static extern IntPtr GetLibraryVersion(IntPtr pCtrl); // const char*

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_GetRobotState")]
        public static extern ROBOT_STATE GetRobotState(IntPtr pCtrl);

        // ── 콜백 등록 (패시브) ──
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_SetOnMonitoringData")]
        public static extern void SetOnMonitoringData(IntPtr pCtrl, TOnMonitoringDataCB cb);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_SetOnMonitoringState")]
        public static extern void SetOnMonitoringState(IntPtr pCtrl, TOnMonitoringStateCB cb);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_SetOnDisconnected")]
        public static extern void SetOnDisconnected(IntPtr pCtrl, TOnDisconnectedCB cb);
    }
}
