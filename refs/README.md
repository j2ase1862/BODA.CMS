# refs/ — DRFL 참고 헤더 (빌드에 포함되지 않음)

`DoosanRobotics/doosan-robot` (`common/include`)에서 받은 DRFL 헤더 원본입니다.
C# P/Invoke 래퍼(`Services/Drfl/`)가 이 정의를 미러링합니다 — 래퍼 수정 시 여기와 대조하세요.

| 파일 | 내용 |
|---|---|
| `DRFS.h` | **데이터 구조체** (`MONITORING_DATA`, `SYSTEM_VERSION` 등). `#pragma pack(1)` |
| `DRFL.h` | flat C API (PascalCase: `_CreateRobotControl`, `_OpenConnection`, `_SetOnMonitoringData` …) |
| `DRFLEx.h` | EX/RT API (snake_case, `_set_on_monitoring_data_ex`, `_connect_rt_control` …) |
| `DRFC.h` | 상수·enum (`NUM_JOINT=6`, `ROBOT_STATE`, `ROBOT_CONTROL` …) |

## 런타임 DLL 배치 (이미 자동화됨)

x64 런타임 DLL 3종은 `../libs/x64/` 에 번들되어 있고, `csproj` 가 빌드 시 출력 폴더로 자동 복사합니다 — **수동 복사 불필요**.

- `DRFLWin64.dll`
- `PocoFoundation64.dll`, `PocoNet64.dll`  ← DRFLWin64.dll 이 직접 의존(PE import 확인)

업데이트 시: `DoosanRobotics/API-DRFL` `library/Windows/64bits/` 에서 받아 `libs/x64/` 의 파일을 교체.
32비트가 필요하면 `library/Windows/32bits/`(DRFLWin32/Poco*.dll) + `csproj` PlatformTarget=x86.

## 핵심 ABI 사실 (래퍼가 의존)

- 호출규약: `extern "C"` + `__cdecl` → C#도 `CallingConvention.Cdecl`
- 구조체 패킹: `pack(1)` → C# `Pack = 1`
- C++ `bool` = 1바이트 → C# `[return: MarshalAs(UnmanagedType.I1)]`
- x64에서 export 명은 헤더 그대로(선행 `_` 포함, 데코레이션 없음)
