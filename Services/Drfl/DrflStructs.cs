using System.Runtime.InteropServices;

namespace DoosanMonitor.Services.Drfl
{
    // ─────────────────────────────────────────────────────────────────────────
    // DRFL 데이터 구조체 — common/include/DRFS.h 를 그대로 미러링.
    //
    // ⚠️ 절대 준수: DRFS.h 는 #pragma pack(1) 구간(57~1170행) 안에서 이 구조체들을
    //    정의한다. 따라서 모든 구조체는 Pack = 1. 한 바이트라도 어긋나면 콜백으로
    //    들어오는 메모리가 통째로 밀려 쓰레기 값이 된다.
    //
    // 상수(DRFC.h):
    //   NUM_JOINT = 6, NUMBER_OF_JOINT = 6, NUM_TASK = 6,
    //   NUM_FLANGE_IO = 6, NUM_BUTTON = 5, MAX_SYMBOL_SIZE = 32
    // 대상 기종 H2017 은 6축이므로 NUM_JOINT = 6 가 맞다.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class DrflConst
    {
        public const int NUM_JOINT = 6;
        public const int NUM_FLANGE_IO = 6;
        public const int NUM_BUTTON = 5;
        public const int MAX_SYMBOL_SIZE = 32;
    }

    /// <summary>_SYSTEM_VERSION (DRFS.h). _GetSystemVersion 으로 채워진다.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct SYSTEM_VERSION
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szSmartTp;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szController;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szInterpreter;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szInverter;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szSafetyBoard;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szRobotSerial;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szRobotModel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szJTSBoard;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DrflConst.MAX_SYMBOL_SIZE)] public string _szFlangeBoard;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ROBOT_MONITORING_STATE
    {
        public byte _iActualMode;   // 0: position control, 1: torque control
        public byte _iActualSpace;  // 0: joint space, 1: task space
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ROBOT_MONITORING_JOINT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualPos; // INC
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualAbs; // ABS
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualVel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualErr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fTargetPos;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fTargetVel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ROBOT_MONITORING_TASK
    {
        // C 원본: float[2][NUM_JOINT] (0: tool, 1: flange) → 평탄화하여 12개.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2 * DrflConst.NUM_JOINT)] public float[] _fActualPos;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualVel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualErr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fTargetPos;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fTargetVel;
        public byte _iSolutionSpace;
        // float[3][3] → 평탄화하여 9개.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public float[] _fRotationMatrix;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ROBOT_MONITORING_TORQUE
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fDynamicTor; // 동역학 토크
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualJTS;  // 관절토크센서
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualEJT;  // 외부 관절토크
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualETT;  // 외부 태스크 힘/토크
    }

    /// <summary>_ROBOT_MONITORING_DATA → MONITORING_CONTROL (DRFS.h). MONITORING_DATA._tCtrl.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MONITORING_CONTROL
    {
        public ROBOT_MONITORING_STATE _tState;
        public ROBOT_MONITORING_JOINT _tJoint;
        public ROBOT_MONITORING_TASK _tTask;
        public ROBOT_MONITORING_TORQUE _tTorque;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MONITORING_MISC
    {
        public double _dSyncTime;  // 내부 클럭 카운터
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_FLANGE_IO)] public byte[] _iActualDI;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_FLANGE_IO)] public byte[] _iActualDO;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public byte[] _iActualBK;   // 브레이크 상태
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_BUTTON)] public uint[] _iActualBT;  // 버튼 상태
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualMC;  // ★ 모터 입력 전류(축별)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DrflConst.NUM_JOINT)] public float[] _fActualMT;  // ★ 모터 온도(축별)
    }

    /// <summary>
    /// _MONITORING_DATA (DRFS.h). _SetOnMonitoringData 콜백이 이 구조체의 포인터를 넘긴다.
    /// 진단 핵심 데이터(축별 토크·전류·온도)가 전부 여기에 들어 있어 _EX 버전이 필요 없다.
    /// (_EX 는 world/user 좌표 프레임만 추가.)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MONITORING_DATA
    {
        public MONITORING_CONTROL _tCtrl;
        public MONITORING_MISC _tMisc;
    }
}
