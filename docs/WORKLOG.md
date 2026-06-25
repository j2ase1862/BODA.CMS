# 작업 로그 (Work Log)

> 세션별 작업 기록. 프로젝트 기준 문서는 [`ROADMAP.md`](../ROADMAP.md)이며, 이 파일은 "무엇을, 왜, 어떻게 했고 무엇을 발견했는지"의 시간순 기록입니다. 최신 항목이 위로 옵니다.

---

## 2026-06-25 — Phase 2(DRFL) 구현 + Phase 1(Modbus) 실기 매핑·앱 통합

### 요약
- DRFL 패시브 모니터링 코드 일체를 작성(헤더 실측 기반, 추측 없음).
- 실 로봇(192.168.1.100)으로 처음 실기 검증 진행.
- **DRFL은 DLL↔컨트롤러 버전 창 문제로 미연결** → 매칭 DLL 확보가 숙제.
- 대신 **Modbus 채널을 실측 매핑하고 앱에 통합 → Basic 등급이 실기에서 동작 확인**(라이브 J1 추종).

### 확정된 사실 (실기)
- **컨트롤러**: DRCF **V2.11.1** (펜던트 패키지 `GV02110100`, v2 라인). → DRFL `DRCF_VERSION=2`.
- **네트워크**: 로봇 `192.168.1.100`, 이 PC 이더넷 `192.168.1.101`. ping/502/12345 모두 OPEN.
- **Modbus 맵** (unit=1, FC03 홀딩, 0-based):
  - `270~275` = 축별 위치 J1~J6 — **int16 × 0.1 = °** (270=`-687` ↔ 펜던트 `-68.70°`로 스케일 확정).
  - `300~305` = 축별 온도(℃ 추정, int16).
  - `400~405` = 축별 전류/토크(int16, 자세 의존, 스케일 미확정).
  - **FC04(입력 레지스터) 미지원**(ILLEGAL FUNCTION). UnitId 0은 무응답, 1·255 응답.
  - 텔레메트리가 단일 int16 → **float 워드오더 이슈 없음**.

### DRFL 미연결 — 원인 진단
- main 브랜치 DLL = **v1.33.3(`GL013303`, v3 프로토콜)** → `_OpenConnection` 자체 타임아웃 실패.
- v2 브랜치 DLL = **v1.1.18(`GL010118`)** → 연결은 통과하나 핸드셰이크 직후 컨트롤러가 세션 종료(`GetSystemVersion` 실패, 0프레임).
- 로봇 모드(Servo On·Auto)·점유(TP만) 모두 배제 → **원인은 DRCF 2.11 ↔ DLL 버전 창**.
- 공개 API-DRFL DLL은 "샘플"이라 미매칭. 공개 Dr.Dart 다운로드센터는 v3.x만 제공.
- **숙제**: DRCF 2.11 매칭 v2 DRFL 라이브러리 확보 — ⓐ Doosan Robot Lab 로그인, 또는 ⓑ 이 컨트롤러와 짝인 DART-Studio 설치 폴더에서 `DRFLWin64.dll`+`Poco*.dll` 복사 → `libs/x64_v2/`에 교체 후 재검증.

### 작성/변경한 산출물
- **DRFL interop** (헤더 `refs/`의 `DRFS.h`/`DRFL.h` 미러):
  - `Services/Drfl/DrflStructs.cs` — `MONITORING_DATA` 등 `Pack=1` 구조체.
  - `Services/Drfl/DrflInterop.cs` — `DRFLWin64.dll` P/Invoke(`__cdecl`, `bool`→`I1`).
  - `Services/DrflMonitorService.cs` — `_SetOnMonitoringData` 패시브 콜백(비개입). `Models/MonitoringSample.cs`.
- **Modbus 텔레메트리**:
  - `Services/ModbusTelemetryService.cs` — 연결 재사용, 10Hz 폴링, 위치 ×0.1° 변환.
  - `Models/ModbusTelemetrySample.cs`.
- **UI/빌드**: `ViewModels/MainViewModel.cs`(DRFL·Modbus 모니터링 토글), `MainWindow.xaml`(두 모니터링 카드), `DoosanMonitor.csproj`(x64 고정 + `libs/x64` DLL 자동 복사 + `tools/**` 제외).
- **참고/번들**: `refs/`(DRFL 헤더 4종 + README), `libs/x64`(v3 DLL), `libs/x64_v2`(v2 DLL).
- **진단 도구**(앱 빌드 제외): `tools/DrflProbe`(DRFL 연결/콜백/라이브러리버전 프로브), `tools/ModbusScan`(홀딩 레지스터 스캐너 + 직전 스냅샷 자동 diff).

### 현재 동작 상태
- ✅ **Modbus 모니터링(Basic)**: 실기에서 라이브 동작, J1 위치 실시간 추종 확인.
- ⏸ **DRFL 모니터링(Pro)**: 코드·DLL 로드·ABI 전부 검증 완료, **매칭 DLL만 있으면 즉시 연결 가능** 상태.

### 다음 작업 후보 (우선순위 메모)
1. (로봇 필요) J2~J6 위치 블록 순서 확정, 온도·전류 스케일 보정.
2. (하드웨어 무관) **Phase 3 — TimescaleDB 적재 파이프라인**: 이 텔레메트리를 시계열로 저장.
3. (DLL 확보 후) DRFL 재검증 → `GetSystemVersion`이 V2.11.1 반환·축별 토크센서 수신 확인.

### 비고
- 테스트용으로 WPF 앱을 실행해 둠(`bin\Debug\net8.0-windows\DoosanMonitor.exe`) — 종료해도 무방.
- 앱 IP 기본값은 두산 문서 기본(`192.168.137.100`). 이 현장 로봇은 `192.168.1.100`이라 실행 후 IP 칸 수정 필요.
