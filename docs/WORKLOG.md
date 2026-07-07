# 작업 로그 (Work Log)

> 세션별 작업 기록. 프로젝트 기준 문서는 [`ROADMAP.md`](../ROADMAP.md)이며, 이 파일은 "무엇을, 왜, 어떻게 했고 무엇을 발견했는지"의 시간순 기록입니다. 최신 항목이 위로 옵니다.

---

## 2026-07-07 (3) — Phase P2: CBM 조건기반 감시 1차

### 요약
- `BODA.CMS.Analytics` 모듈 신설 — 기준선 학습 + 편차/추세 임계값 알림 엔진. 벤더 무관(공용 프레임만 소비), Basic 채널의 원시 신호(VendorRaw)도 감시.
- WPF에 건강도 칩·CBM 알림 리스트 탑재. 시뮬레이터 결함 주입으로 학습→알람 경로 검증.

### 설계 (CbmMonitor)
- **파이프라인**: 프레임 → 1초 집계(고주파 노이즈 억제) → 신호·축별 기준선(Welford 평균/σ, 기본 60집계) → z-점수 판정.
- **판정 2종**: 급변 = 즉시값 |z|≥4 연속 3회 → Alarm / 드리프트 = EWMA(α=0.1) |z|≥3 연속 10회 → Warning. 활성 알림이 |z|<1.5로 연속 5회 안정되면 Info "복귀" 통지(자동 해제).
- **과민 방지**: σ 하한 = max(σ, 1e-3, 1%·|μ|) — 거의 상수인 신호(원시 카운트 등)의 미세 잡음 알람 차단. 위치·속도는 동작 의존이라 기본 제외.
- **건강도**: 최악 |z| 기반 휴리스틱 — z≤1 → 100점, z≥5 → 0점 선형. 카드 칩 색상(초록/주황/빨강) 연동.
- 신호 열거를 `Core.TelemetrySignals`로 승격 — 판독 표·차트·CBM이 같은 라벨 체계 공유.

### 검증
- 유닛테스트 +7 (총 29): 러닝스탯, 학습→감시 전환, 급변 디바운스·1회 발화, 복귀 통지, 드리프트 경로 분리 검증, 위치/속도 제외, VendorRaw 감시.
- 시뮬레이터 결함 주입(가상 Pro, t=100s J3 토크/전류 +35% 램프)으로 실행 검증 — 60초 학습 후 J3 알람 발화, 카드 칩 건강도 하락·알림 리스트 표기 확인.

### 빌드 경로 단일화 (재발 방지)
- 구버전 exe 실행 사고가 세 번째 반복돼 근본 수정: 앱 csproj에 `AppendPlatformToOutputPath=false` — 솔루션(x64) 빌드든 프로젝트 빌드든 **항상 `bin\Debug\net8.0-windows\`** 한 곳. `bin\x64\` 삭제.
- 교훈: 실행 중인 앱 인스턴스가 exe를 잠근 상태의 빌드는 조용히 낡은 출력을 남길 수 있다 — 데모 전 `Get-Process BODA.CMS | Stop-Process` 후 재빌드가 안전.

### 다음 작업
- P2 잔여: 기준선 영속화, Collector 통합(알림 DB 저장), 다중 로봇 뷰(P5와 연계).
- P3(ML 이상탐지) 착수 가능 — 리샘플링 규약(§7)부터.

---

## 2026-07-07 (2) — Phase P1: 프로젝트 물리 분리 + Collector + TimescaleDB 파이프라인

### 요약
- P0 계약 위에 **P1 코드 완료**: 모듈 물리 분리, headless 수집기, 재연결 규약, TimescaleDB 스키마/적재.
- 시뮬레이터 2채널로 end-to-end 검증(dry-run): 펌프 → 버퍼 → 배치 플러시 정상. **실 DB 적재 검증만 남음**(개발 PC에 PostgreSQL 부재).

### 작업 내용
- **물리 분리**: `Core`(net8.0) / `Comms`(NModbus 헬퍼 — `ModbusConnectionService` 이동, 네임스페이스 `BODA.CMS.Comms`) / `Drivers.Doosan`(DRFL 네이티브 DLL 전이 복사 소유) / `Drivers.Simulated` / 루트 WPF는 참조만. DrflProbe도 링크 컴파일 → 모듈 참조로 전환. **windows 타깃은 WPF뿐** — §7 오픈 퀘스천 해소.
- **재연결 시맨틱**(계약 문서화 + 드라이버 수정):
  - 규약: Faulted/해제 후 `ConnectAsync` 재호출 가능, 이전 세션 잔재는 드라이버가 정리.
  - 두산 Modbus: 연속 읽기 오류 20회 → Faulted(TcpClient.Connected stale 대응), `ownsConnection` 모드(수집기용 — 해제 시 세션 닫아 재연결 시 새 소켓).
  - 두산 DRFL: **Faulted 후 재연결 불가 버그 수정** — 내부 `_connected` 플래그 잔재로 `Connect()`가 조용히 no-op 되던 것을 `ConnectAsync` 진입 시 선정리로 해결.
- **Collector** (`Collector/`, Worker SDK): `appsettings.json Collector:Robots[]`(RobotId/Vendor/Host/Port/Channels 필터) → 벤더 카탈로그(Program.cs 컴포지션 루트) → 채널별 펌프(지수 백오프 1s→30s) → `FrameBuffer`(bounded 100k, 포화 시 DropOldest — 수집이 저장을 안 기다림) → `StorageWorker`(BatchSize/FlushInterval 배치).
- **저장소**: `TimescaleFrameStore` — `telemetry_frames` 단일 테이블(벤더별 테이블 금지), 태그 컬럼 + 축별 `real[]` + `vendor_raw jsonb`, 바이너리 COPY. `create_hypertable` 시도 후 확장 없으면 일반 테이블 폴백. `Storage:Enabled=false` → `LogFrameStore` dry-run.
- **테스트 +4** (총 22): 프레임→행 매핑(널 규약·jsonb 왕복), 백오프(지수·상한·음수 안전).

### 검증
- 전체 솔루션(7프로젝트) 빌드 경고 0, 테스트 22/22.
- Collector 12초 dry-run: `sim-01/basic` ~10Hz, `sim-01/pro` ~65Hz 수집·플러시 확인. (pro가 100Hz 미달인 건 시뮬레이터의 `Task.Delay(10ms)`가 Windows 타이머 해상도(15.6ms)에 걸리기 때문 — 실 드라이버는 콜백 기반이라 무관.)

### 다음 작업
1. 실 TimescaleDB 인스턴스 확보(도커 or 기존 BODA 스택) → `Storage:Enabled=true` 적재 검증.
2. Phase P2 — CBM(기준선 학습·임계값 알림) 착수 가능(시뮬레이터로 개발).

---

## 2026-07-07 — Phase P0: 드라이버 추상화 리팩터링 (멀티벤더 계약 확정)

### 요약
- ROADMAP을 멀티벤더 플랫폼 구조로 개편(§4 플랫폼 공통 / §5 벤더 모듈 / §6 온보딩 절차)한 데 이어, **P0 리팩터링을 코드로 완료**.
- 이제 파이프라인 위쪽(VM·UI)은 `IRobotTelemetrySource` 계약과 `RobotTelemetryFrame`만 안다 — 벤더 타입은 컴포지션 루트(`MainWindow.xaml.cs`)에서만 등장.

### 작업 내용
- **Core 계약** (`Core/Telemetry/`, `BODA.CMS.Core.Telemetry`): `IRobotTelemetrySource`(State/FrameReceived/StateChanged/Notification), `RobotTelemetryFrame`(정규화 단위 + `VendorRaw` 딕셔너리), `RobotCapabilities`(+`DefaultPort`), `RobotEndpoint`, `ProductTierEvaluator`(capability → None/Basic/Pro).
- **두산 드라이버** (`Drivers/Doosan/`):
  - `DoosanModbusSource`(Basic) — 구 `ModbusTelemetryService` 대체. 위치만 ×0.1° 정규화, 온도·전류/토크는 `VendorRaw["temp_raw"/"cur_raw"]`에 원시 보존(스케일 미확정 → `Has*=false` 규약 준수). 공유 Modbus 세션이 외부에서 닫히면 `Faulted`로 전환.
  - `DoosanDrflSource`(Pro) — `DrflMonitorService`를 감싸 공용 프레임 방출(JTS/동역학/외란 토크·전류·온도, DRFL 원본 단위가 규약과 일치해 환산 없음).
  - `DrflMonitorService`·`MonitoringSample`·interop은 드라이버 내부로 이동(구 `Services/Drfl`, `Models`에서). `ModbusTelemetrySample` 제거.
- **UI**: 채널 카드 1장 = `TelemetrySourceViewModel` 1개(계약에만 의존, 카드별 포트 입력·등급 라벨 표시), `MainWindow.xaml`은 `ItemsControl` 렌더 → **벤더 추가 시 XAML 수정 불필요**.
- **테스트**: `tests/BODA.CMS.Tests`(xUnit, x64) 신설 — 등급 판정 9건 + Modbus 프레임 변환 4건 + 판독 필터 5건 = **18건 통과**. 두산 Modbus=Basic·DRFL=Pro 판정을 잠금. `InternalsVisibleTo`로 내부 매핑 함수 검증.
- **도구**: `tools/DrflProbe` 링크 경로/네임스페이스 갱신(빌드 확인). `DoosanMonitor.sln` 잔재 삭제.
- **UI/UX 확장(같은 세션)**:
  - **제조사 선택**: `VendorDescriptor` 카탈로그(컴포지션 루트 등록) + 연결 카드의 콤보박스. 전환 시 실행 중 카드를 정지·해제 후 새 벤더의 채널 카드로 교체. JAKA/Rokae는 드라이버 구현 후 카탈로그 1항목 추가로 노출.
  - **표시 신호 선택**: 카드별 체크박스(`SignalToggle`) — 첫 프레임에 실제 존재하는 신호로 자동 구성(벤더 하드코딩 없음), 해제 시 판독 표에서 즉시 제외(마지막 프레임으로 재렌더). DRFL 외란토크(`외란Nm`) 행 추가.
  - **창 크기**: 1000×920, 최소 760×700.
- **시뮬레이터 드라이버** (`Drivers/Simulated/SimulatedRobotSource.cs`): 하드웨어 없이 파이프라인·UI를 구동하는 가상 벤더. 사인파 조그 + 자세 의존 모델 토크 + 노이즈 합성. 프로필 2종 — 가상 Basic(10Hz, 범용 모사: 위치 정규화 + temp_raw/cur_raw)·가상 Pro(100Hz, 네이티브 모사: 토크/전류/온도 정규화). **코어·UI 수정 없이 카탈로그 등록만으로 벤더가 붙는 것을 실증** — 벤더 격리 원칙 검증 완료. `VendorDescriptor.ToString`을 DisplayName으로 오버라이드(콤보 항목·UIA 접근성 이름 정합).
- **빌드 출력 경로 주의**: 솔루션 빌드(`Debug|x64`)와 프로젝트 빌드가 서로 다른 폴더(`bin\x64\Debug` vs `bin\Debug`)에 떨어져 구버전 exe 실행 사고가 반복됨. → **P2에서 `AppendPlatformToOutputPath=false`로 단일화: 항상 `bin\Debug\net8.0-windows\`** (아래 (3) 참조).
- **라이브 차트** (ScottPlot.WPF 5.0.55, `Views/SignalChartView`):
  - 카드별 표↔차트 토글. 차트는 선택 신호 1개를 축별 라인(J1~J6)으로, 최근 30초 스크롤 창. 신호 선택 콤보는 기존 신호 라벨 재사용(벤더 무관).
  - 데이터 경로: 드라이버 스레드 → VM의 bounded ConcurrentQueue(전 샘플, 상한 2048) → 차트 뷰 UI 타이머(10Hz)가 드레인 → 자체 링 버퍼(축별 double[]) → ScottPlot `Signal`이 그대로 렌더. 표 갱신(10Hz throttle)과 독립.
  - ⚠️ **ScottPlot `DataStreamer`는 사용 금지**: `Add()` 내부 NRE로 앱 크래시 실측(인라인돼 스택엔 호출부만 남음). `Signal` + 자체 버퍼로 대체. 차트 예외는 카드 단위로 격리(타이머 정지 + `%TEMP%\BODA.CMS.crash.log`).
  - 축 라벨은 `Malgun Gothic` 지정(ScottPlot 기본 폰트에 한글 글리프 없음). `VendorDescriptor`/`SignalToggle`에 ToString 오버라이드(콤보·UIA 이름 정합).
  - App에 `DispatcherUnhandledException` 핸들러 추가 — 모니터링 앱이 UI 예외 한 번으로 죽지 않게 로그 + 계속 실행.

### 검증
- `dotnet build BODA.CMS.sln` 경고 0·오류 0, `dotnet test` 18/18 통과, DrflProbe 단독 빌드 통과.
- 실기 검증(로봇 필요)은 별도: Modbus 카드 라이브 폴링이 기존과 동일하게 동작하는지 확인 필요(로직은 이식이며 신규 작성 아님).

### 다음 작업
1. **Phase P1**: TimescaleDB 스키마(공용 프레임 기준) + `BODA.CMS.Collector` 분리.
2. (로봇 필요) 온도·전류/토크 스케일 확정 → `DoosanModbusSource`에서 정규화 승격 + capability 상향.
3. (DLL 확보 후) DRFL 재검증 — §5.1 검증 노트 경로 ⓐⓑ.

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
