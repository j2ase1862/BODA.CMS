# BODA.CMS — 멀티벤더 협동로봇 상태 진단 플랫폼 로드맵

> 이 문서는 프로젝트의 단일 기준(source of truth)입니다. 이 저장소에서 작업하는 어시스턴트(Claude Code 등)는 먼저 이 문서를 읽고, 작업 완료 시 해당 체크박스를 갱신하세요.
>
> **문서 구조**: §4 **플랫폼 공통 로드맵**(벤더 무관 파이프라인·UI·제품화)과 §5 **벤더 드라이버 모듈**(제조사별 수집 계층)을 분리해서 관리합니다. 새 제조사 지원은 §6 온보딩 절차를 따라 §5에 모듈 섹션을 추가하는 방식으로 확장합니다.

---

## 1. 프로젝트 개요

- **무엇**: 협동로봇의 상태를 비개입 방식으로 모니터링하고, 단계적으로 예지보전(Predictive Maintenance)까지 확장하는 **멀티벤더 플랫폼**. 두산은 1호 레퍼런스 벤더이며, 벤더 드라이버 모듈을 추가하는 것만으로 타 제조사 로봇을 지원한다.
- **목적**: 대량이 아닌 **소량이라도 지속 가능한(구독형) 매출** 창출.
- **차별화**: 단순 센서 키트가 아니라 ① 라인 운영 통합 + ② 머신비전 기반 진단(바심의 핵심 역량)을 결합. 핵심 가치는 "하드웨어"가 아니라 "통합 + 소프트웨어 + 구독 서비스". 멀티벤더 지원 자체가 셋째 차별화 축 — 혼류 라인(여러 제조사 로봇 혼재)을 하나의 대시보드로 커버.
- **제품 등급(목표)** — 벤더별 하드코딩이 아니라 드라이버가 선언하는 **capability 기준으로 자동 결정**:
  - **Basic** — 범용 프로토콜 채널(Modbus 등) 기반 모니터링. 축별 위치 + 온도 + 전류/토크(가용 시), ≥1Hz. 전 기종 무코딩 커버.
  - **Pro** — 제조사 네이티브 API 채널 기반 심층 진단. 축별 전류·토크(가능하면 토크센서 실측), ≥10Hz.
  - **Plus** — 외부 진동 센서(kHz FFT) + 비전 진단. 벤더 무관(플랫폼 공통 계층에서 제공).

---

## 2. 아키텍처 — 코어/드라이버 분리 (모듈식 설계의 핵심)

**스택**
- 플랫폼: .NET 8 (`net8.0-windows`, 수집기 분리 시 코어는 `net8.0` 유지 검토)
- UI: WPF (MVVM), 제품화 단계에서 웹 대시보드(ASP.NET Core + Blazor — BODA.VMS.Web 자산 재사용) 추가
- 시계열 저장: TimescaleDB (기존 BODA 스택)
- AI: Python 학습 → **ONNX** export → .NET ONNX Runtime 추론 (C# 영역 안에서 처리)
- 벤더별 통신: 각 드라이버 모듈이 자체 의존성 보유 (두산: NModbus + DRFL P/Invoke)

**계층 구조 (아래에서 위로)** — 벤더 의존성은 드라이버 계층에서 끝난다. 그 위는 전부 벤더 중립.

```
현장 설비 (제조사별 협동로봇 + 컨트롤러)
   ↓ 수집 (벤더 의존 영역 — 여기까지만)
┌─ 벤더 드라이버 모듈 ─────────────────────────────┐
│  Drivers.Doosan   (Modbus 폴링 + DRFL 패시브 콜백)   │
│  Drivers.Jaka     (계획)                             │
│  Drivers.Rokae    (계획)                             │
└──────────── IRobotTelemetrySource 계약 ─────────────┘
   ↓ 정규화된 RobotTelemetryFrame (벤더 중립)
TimescaleDB (축별 시계열, 벤더/기종 태그)
   ↓ 지능
AI 계층 (CBM → 이상탐지 → RUL) — Python 학습 / ONNX 추론
   ↓ 서비스
.NET WebAPI · SignalR
   ↓ 표현
WPF 모니터링 UI (+ 웹 대시보드 + 향후 3D 트윈)
```

**모듈 구성 (목표 프로젝트 구조)**

| 모듈 | 책임 | 벤더 의존 |
|---|---|---|
| `BODA.CMS.Core` | 텔레메트리 계약(`IRobotTelemetrySource`, `RobotTelemetryFrame`, `RobotCapabilities`), 정규화 규약, 등급 판정 | 없음 |
| `BODA.CMS.Drivers.Doosan` | Modbus 맵 + 스케일, DRFL P/Invoke, 두산 프레임 → 공용 프레임 변환 | 두산 |
| `BODA.CMS.Drivers.{Vendor}` | (벤더 추가 시) 해당 벤더 채널 + 변환 | 해당 벤더 |
| `BODA.CMS.Collector` | headless 수집 서비스: 드라이버 로드, 버퍼링, TimescaleDB 적재, 재연결 | 없음 |
| `BODA.CMS.Analytics` | CBM 기준선·임계값, ONNX 추론 | 없음 |
| `BODA.CMS` (WPF) | 모니터링 UI. Collector/WebAPI를 구독하는 뷰어 | 없음 |

> 현재는 단일 `BODA.CMS.csproj` 안에서 폴더·네임스페이스로 분리돼 있음(`Core/Telemetry/`, `Drivers/Doosan/` — P0 완료). 프로젝트 물리 분리는 P1(수집기 분리)에서 수행. **프로젝트 물리 분리보다 인터페이스 계약이 먼저다** — 계약이 섰으므로 물리 분리는 언제든 가능. 벤더 드라이버 타입은 컴포지션 루트(`MainWindow.xaml.cs`)에서만 등장한다.

**드라이버 계약 (P0에서 확정 — `Core/Telemetry/` 구현이 원본, 이 블록은 요약)**

```csharp
// 모든 벤더 드라이버가 구현하는 단일 계약
public interface IRobotTelemetrySource : IAsyncDisposable
{
    RobotCapabilities Capabilities { get; }            // 이 드라이버·이 채널이 뭘 줄 수 있는가
    TelemetrySourceState State { get; }                // Disconnected/Connecting/Connected/Faulted
    event EventHandler<RobotTelemetryFrame> FrameReceived; // ⚠️ 드라이버 스레드에서 발화 — UI 마샬링은 구독자 책임
    event EventHandler<TelemetrySourceState> StateChanged;
    event EventHandler<string> Notification;           // 로그성 통지(컨트롤러 버전·로봇 상태·일시 오류)
    Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default);
    Task DisconnectAsync();
}

// 접속 대상 — Port 미지정(null) 시 드라이버 기본 포트(Capabilities.DefaultPort) 사용
public sealed record RobotEndpoint(string Host, int? Port = null);

// 벤더 중립 정규화 프레임 (기존 MonitoringSample/ModbusTelemetrySample의 상위 통합형)
public sealed class RobotTelemetryFrame
{
    DateTime ReceivedAtUtc;      // PC 수신 시각 (UTC)
    double? ControllerClock;     // 컨트롤러 내부 클럭(초) — 지원 벤더만
    string VendorId;             // "doosan", "jaka", ...
    string ChannelId;            // "modbus", "drfl", ... (같은 로봇의 다중 채널 구분)
    float[] JointPositionDeg;    // ° (필수 — null 배열은 "미제공" 의미, 0 채움 금지)
    float[]? JointVelocityDegS;  // °/s
    float[]? MotorCurrentA;      // A (스케일 확정 채널만; 미확정이면 VendorRaw에)
    float[]? JointTorqueNm;      // Nm — 토크센서 실측 (있는 기종만)
    float[]? ModelTorqueNm;      // Nm — 동역학 모델 추정 토크
    float[]? ExternalTorqueNm;   // Nm — 외란(외부) 토크 추정
    float[]? TemperatureC;       // ℃
    IReadOnlyDictionary<string, double[]>? VendorRaw; // 스케일 미확정 원시값 — 신호명 키(드라이버 정의)
                                 // 예: 두산 Modbus "temp_raw"(300~305), "cur_raw"(400~405)
}

// capability 선언 → 제품 등급 자동 판정(ProductTierEvaluator)의 근거
public sealed class RobotCapabilities
{
    string VendorId; string ChannelId; string DisplayName;
    int AxisCount;
    double NominalSampleRateHz;
    int DefaultPort;             // 채널 기본 접속 포트
    bool HasJointTorqueSensor;   // 실측 토크 (Rokae 전축 O, 두산 JTS 기종 O)
    bool HasMotorCurrent;
    bool HasTemperature;
    bool IsPassive;              // 비개입 보장 채널인가 (라이브 감시 허용 조건)
}
```

**정규화 규약 (모든 드라이버가 준수)**
- 단위: 위치 **°**, 속도 **°/s**, 전류 **A**, 토크 **Nm**, 온도 **℃**. 단위 환산은 **드라이버 책임** — 파이프라인 위쪽은 원시 스케일을 모른다.
- 스케일 미확정 신호는 환산하지 말고 `VendorRaw`에 보존 (추정 스케일로 오염 금지). 스케일 확정 시 드라이버만 수정.
- capability 플래그(`Has*`)는 **정규화 완료된 신호만** true로 선언한다. `VendorRaw`에만 있는 스케일 미확정 신호는 false — 스케일 확정 시 드라이버가 플래그와 매핑을 함께 올린다(등급 자동 판정도 그때 상향). 예: 두산 Modbus 전류/토크는 스케일 확정 전까지 `HasMotorCurrent=false`.
- 축 인덱스: J1=0 부터. 타임스탬프는 PC UTC 기준, 컨트롤러 클럭은 보조.

**채널 이중화 패턴 (두산에서 검증 → 전 벤더 공통 패턴으로 승격)**
- **범용 채널**(Modbus/OPC UA 등 표준 프로토콜) = Basic 등급. 무코딩·저위험·전 기종.
- **네이티브 채널**(제조사 SDK) = Pro 등급. 고분해능·심층 신호.
- 한 로봇에 두 채널을 **동시 연결 가능**해야 함 (`ChannelId`로 구분) — 범용 채널은 연결 프로브/베이스라인, 네이티브는 진단 본선.

---

## 3. 플랫폼 공통 원칙 (반드시 준수 — 벤더 무관)

- **⚠️ 비개입 원칙 (가장 중요)**: 이 솔루션은 **로봇의 실제 생산 작업을 절대 방해하면 안 된다.**
  - 라이브 모니터링은 **패시브(읽기 전용) 채널만** 사용. 드라이버는 `IsPassive=true`인 채널만 라이브 경로에 등록할 수 있다.
  - 로봇을 장악하는 실시간 제어 모드(두산 RT 제어 등)는 **건강 기준 시그니처 오프라인 캐릭터라이제이션 전용**. 라이브 감시에 사용 금지. 이 구분은 벤더가 늘어나도 동일하게 적용.
- **무코딩 원칙**: 로봇 쪽에는 단 한 줄의 프로그램도 다운로드하지 않는다. 모든 통신은 PC 측에서 컨트롤러로. (벤더별 예외가 필요해지면 §5 해당 모듈에 명시하고 고객 고지.)
- **벤더 격리 원칙**: 벤더 SDK 타입·레지스터 주소·스케일 상수가 `Drivers.{Vendor}` 밖으로 새어 나가면 안 된다. UI·저장·분석 코드에 벤더 분기(`if (vendor == ...)`)가 생기면 설계 실패 신호.
- **PdM 성숙도 사다리 (순서대로 올라간다)**:
  1. 모니터링 (descriptive)
  2. 조건기반 감시 CBM (임계값·추세, ML 불필요) ← 현장 가치의 대부분
  3. ML 이상탐지 (비지도, 고장 라벨 불필요)
  4. 잔여수명 RUL 예측 (고장 이력 충분히 축적 후) ← 대부분 3단계까지가 현실
- **언어/배포**: C# 자산 최대 활용. AI 학습만 Python, 추론은 ONNX로 .NET 안에서.

---

## 4. 플랫폼 공통 로드맵 (벤더 중립 — Phase P)

> 벤더별 수집 작업은 §5로. 여기는 어떤 벤더가 붙어도 변하지 않는 층.

### Phase P0 — 드라이버 추상화 리팩터링 ✅ 완료 (2026-07)
> 목표: §2 드라이버 계약을 코드로 확정하고, 기존 두산 구현을 계약 뒤로 이동. 이후 모든 파이프라인은 계약에만 의존.

- [x] `IRobotTelemetrySource` / `RobotTelemetryFrame` / `RobotCapabilities` / `RobotEndpoint` 확정 (`Core/Telemetry/`, 네임스페이스 `BODA.CMS.Core.Telemetry`)
- [x] `ModbusTelemetryService` → `DoosanModbusSource`로 개편: 공용 프레임 방출 (위치 ×0.1° 환산은 드라이버 내부, 온도·전류/토크 원시값은 `VendorRaw["temp_raw"/"cur_raw"]`). `ModbusTelemetrySample` 제거.
- [x] `DrflMonitorService` → `DoosanDrflSource`로 개편: JTS→`JointTorqueNm`, 동역학→`ModelTorqueNm`, 외란→`ExternalTorqueNm`, `_fActualMC`→`MotorCurrentA`, `_fActualMT`→`TemperatureC` (DRFL 원본 단위가 규약과 일치 — 환산 없음)
- [x] `MainViewModel`이 소스를 `IRobotTelemetrySource` 컬렉션으로만 다룸: 채널 카드 1장 = `TelemetrySourceViewModel` 1개, XAML은 `ItemsControl` 렌더 → 벤더 추가 시 UI 수정 불필요. 드라이버 생성은 컴포지션 루트(`MainWindow.xaml.cs`)에서만.
- [x] 등급 판정: `ProductTierEvaluator`(capability → None/Basic/Pro) + 유닛테스트 13건 (`tests/BODA.CMS.Tests`, xUnit — 두산 Modbus=Basic·DRFL=Pro 잠금 포함)
- [x] `MonitoringSample`은 `Drivers/Doosan/` 내부 타입으로 강등 (DRFL 콜백 구조체의 1차 스냅샷 — DrflProbe 도구가 공유)

### Phase P1 — 데이터 파이프라인 & 저장 + 수집기 분리 ← **다음 작업**
- [ ] TimescaleDB 스키마 설계(hypertable): 공용 프레임 기준 — 벤더/기종/채널 태그 + 축별 토크·전류·온도·위치, 타임스탬프. **벤더별 테이블 금지, 태그로 구분.**
- [ ] `BODA.CMS.Collector` headless 수집 서비스 분리 (Worker Service): 드라이버 로드 → 버퍼링/배치 적재. WPF는 뷰어로 전환.
- [ ] 연결 끊김·재연결 복구 로직 (드라이버 계약에 재연결 시맨틱 포함)
- [ ] 다중 로봇 동시 수집 (엔드포인트 N개 구성 파일 기반)

### Phase P2 — 조건기반 감시(CBM)
- [ ] 정상 사이클 동안 **기준선 학습**(축별 토크·전류 평균/분산) — 공용 프레임 기준이므로 벤더 무관
- [ ] 기준선 대비 편차·추세 기반 임계값 알림
- [ ] WPF 대시보드: 축별 추세 차트, 건강도 게이지, 알림 리스트 (다중 로봇·다중 벤더 뷰)

### Phase P3 — ML 이상탐지
- [ ] 시계열 특징 추출(피처 엔지니어링) — 샘플링 주기 차이(벤더별 1~100Hz)를 흡수하는 리샘플링 규약 포함
- [ ] Python 학습(Anomalib/sklearn/PyTorch, 비지도) → **ONNX export**
- [ ] .NET ONNX Runtime으로 앱 내 추론
- [ ] 이상 점수를 UI/알림에 연동

### Phase P4 — 비전 진단 (바심 차별화, 벤더 무관)
- [ ] 카메라 기반 반복정밀도 드리프트·엔드이펙터 마모 진단 PoC
- [ ] 센서 데이터와 비전 데이터 융합 진단

### Phase P5 — 제품화
- [ ] 구독/라이선스 모델 (등급 = capability 자동 판정과 연동)
- [ ] 웹 대시보드 (ASP.NET Core + Blazor, BODA.VMS.Web 자산 재사용) — 원격/다중 사용자 모니터링
- [ ] 패키징·배포·업데이트 (드라이버 모듈 단위 배포 검토)

---

## 5. 벤더 드라이버 모듈

> 모듈별로 독립 진행. 각 모듈의 목표는 동일: **§2 계약을 구현하고 §6 온보딩 체크리스트를 통과하는 것.**

### 5.1 두산 로보틱스 (1호 — 레퍼런스 구현, 진행 중)

**대상 하드웨어**: H2017 / 컨트롤러 CS-01 / DRCF V2.11.1(패키지 `GV02110100`, v2 라인) → DRFL 빌드 `DRCF_VERSION=2`

**채널 구성**
- **범용(Basic)**: Modbus TCP — NModbus 3.x. 축별 위치·온도·전류/토크 실측 확인 → **Basic 등급 결정 게이트 통과.**
- **네이티브(Pro)**: DRFL(Doosan Robot Framework Library, 오픈소스) 패시브 모니터링 콜백(`_SetOnMonitoringData`). RT 제어 모드(`connect_rt_control`, 1kHz)는 오프라인 캐릭터라이제이션 전용.

**진행 상태**
- [x] **D-Phase 0 — WPF 스켈레톤 + 연결 테스트** (`DoosanMonitor` 솔루션 생성 완료)
  - MVVM 구조: `ViewModelBase`, `AsyncRelayCommand`
  - `Services/ModbusConnectionService` — TCP/Modbus 연결을 **유지**(이후 폴링·모니터링에서 재사용)
  - `MainWindow` — IP/포트 입력, 연결 버튼, 상태등(회색/주황/초록/빨강), 로그
- [~] **D-Phase 1 — Modbus 프로브 & 맵 + 앱 통합** — 실기 매핑 완료, 앱 기능화 완료(**스케일 보정만 남음**)
  - 매핑: `tools/ModbusScan` 콘솔로 unit=1·FC03 확인, J1 조그 격리로 **위치(270~275)·온도(300~305)·전류/토크(400~405)** 블록 확정. 위치 **0.1°/count 교정**. FC04 미지원.
  - 통합: `Services/ModbusTelemetryService`(연결 재사용, 10Hz 폴링) + `Models/ModbusTelemetrySample` + MainWindow "Modbus 텔레메트리" 카드(라이브 축별 표)
  - ✅ **실기 검증 완료**: 실제 로봇(192.168.1.100)에서 앱 라이브 폴링 동작, J1 조그 시 위치 실시간 추종 확인
  - [ ] 온도·전류/토크 스케일 확정 (조그로 비영 값 확보 후 펜던트 대조)
  - [ ] J2~J6 개별 조그로 270~275 외 블록의 축 순서 확정, 속도 레지스터 탐색
- [~] **D-Phase 2 — DRFL 연동(패시브 모니터링)** — 코드 완료, **실기 검증 대기 (DRCF 2.11 매칭 DLL 필요 — 아래 검증 노트)**
  - `Services/Drfl/` — `DrflStructs.cs`(DRFS.h Pack=1 미러), `DrflInterop.cs`(`DRFLWin64.dll` P/Invoke, `__cdecl`)
  - `Services/DrflMonitorService.cs` — `_SetOnMonitoringData` 패시브 콜백 → `MonitoringSample`(축별 위치/토크/전류/온도). **비개입**(명령 권한 미취득: access control/SetRobotMode/SetRobotControl 미호출)
  - `MainWindow` — DRFL 모니터링 카드(상태등 + 라이브 축별 표, 100Hz→10Hz throttle, 네이티브 스레드→Dispatcher 마샬링)
  - `libs/x64/` — DRFL+Poco 네이티브 DLL 번들, 빌드 시 자동 복사(로드/심볼 검증 완료). csproj **x64 고정**
  - [ ] **실기 검증(하드웨어 필요)**: 매칭 DLL 확보 → 콜백 수신·필드 정합(`GetSystemVersion`로 DLL↔v2.11 대조)·축별 값 sanity 확인 ← D-Phase 2 종료 게이트
  - [ ] (선택) RT 1kHz 경로: `connect_rt_control` → `set_rt_control_output("v1.0", 0.001f, 4)` → `start_rt_control`로 **건강 기준 시그니처** 정밀 측정. ⚠️ 오프라인 전용
- [ ] **D-Phase 3 — §6 온보딩 체크리스트 1호 통과**: 드라이버 개편·capability 선언 등 구현 작업은 **§4 Phase P0에서만 추적**(중복 기재 금지). P0 완료 후 두산이 §6 체크리스트를 레퍼런스로 통과하면 종료.

> **Modbus 홀딩 레지스터 맵 (CS-01, DRCF V2.11.1, unit=1, FC03, 0-based)** — `tools/ModbusScan` 실측:
> | 주소(0-based / 4xxxx) | 내용 | 형식 | 비고 |
> |---|---|---|---|
> | 270~275 / 40271~40276 | **축별 위치 J1~J6** | int16, **×0.1 = °** | 270=J1 격리 확정. **스케일 0.1°/count 확정**(270=-687 ↔ 펜던트 -68.70°) |
> | 300~305 / 40301~40306 | **축별 온도(℃)** | int16, ~1℃/count(추정) | 정지 시 일제히 냉각 드리프트로 확인 |
> | 400~405 / 40401~40406 | **축별 전류/토크** | int16 | 자세/중력부하 의존, 동적. 스케일 미확정 |
> | 256~260 / 40257~40261 | 상태/모드 추정 | int16 | 259가 카운터성 변동 |
> | 290~294 | 미확정(좌표/속도 후보) | — | J1 회전 시 소폭 변동 |
> | 450~479 | 이벤트/큐 버퍼(텔레메트리 아님) | — | 묶음이 0으로 빠짐 → 제외 |
> - float 워드 오더 이슈 **소멸**: 텔레메트리가 단일 int16 스케일값으로 노출(2워드 float 아님)

> **DRFL 실기 1차 검증 결과(192.168.1.100)** — `tools/DrflProbe` 콘솔 프로브로 실측:
> - 네트워크: ping OK, 502/12345 OPEN.
> - **DLL 버전 정합 확인**: API-DRFL `main` DLL = **v1.33.3(`GL013303`, v3 프로토콜)** → `_OpenConnection` 타임아웃 실패. `v2` 브랜치 DLL = **v1.1.18(`GL010118`)** → 연결은 통과하나 컨트롤러가 핸드셰이크 직후 세션 종료(`GetSystemVersion` 실패, 0프레임).
> - 양쪽 다 "Timeout"으로 떨어짐. **모드 배제됨**: Servo On + 자동(Autonomous) 모드에서도 동일 → 로봇 모드/점유 문제 아님. **원인 확정: DRCF 2.11 ↔ DRFL DLL 버전 창.** 컨트롤러는 v2 프로토콜(v3 DLL은 OpenConnection 자체 실패)이나, 보유 v2 DLL(1.1.18)이 2.11엔 너무 구버전.
> - **다음 작업: DRCF 2.11 호환 v2 계열 DRFL 라이브러리 확보**(공개 저장소엔 1.1.18 ↔ 1.33만 존재, 중간 공백). 입수 경로: ⓐ Doosan Robot Lab(robotlab.doosanrobotics.com) 로그인 → 이전 버전 다운로드, 또는 ⓑ 이 컨트롤러와 짝인 DART-Studio 설치 폴더의 DRFL DLL 복사. 입수한 `DRFLWin64.dll`+매칭 `Poco*.dll`을 `libs/x64_v2/`에 교체하면 즉시 재검증 가능(나머지는 전부 검증 완료).
> - Modbus(502): UnitId 1/255 · FC03 응답, FC04 미지원.

**두산 DRFL 구조체 확정 사항**: 진단 데이터는 `MONITORING_DATA`(non-EX)에 전부 존재 — 토크 `_tCtrl._tTorque`(JTS/동역학/외부), 전류 `_tMisc._fActualMC`, 온도 `_tMisc._fActualMT`. EX는 world/user 좌표만 추가 → **non-EX 채택**. 헤더는 `refs/`에 보관.

### 5.2 JAKA (2호 — 계획)

> 선정 이유: 인터페이스가 가장 개방적, Modbus 기본 ON → Basic 채널 온보딩 비용 최소 예상.

- [ ] 사전 조사: 컨트롤러 Modbus 맵 문서 확보 (JAKA는 공식 레지스터 맵 공개 — 두산처럼 프로브로 역공학할 필요 없는지 확인)
- [ ] 사전 조사: 네이티브 SDK(JAKA SDK, TCP 10000/10001 포트) — 패시브 모니터링 가능 범위, 축별 전류·토크 노출 여부
- [ ] 하드웨어 확보 계획 (실기 or 데모 유닛 대여)
- [ ] `Drivers.Jaka` 구현 → §6 온보딩 체크리스트

### 5.3 Rokae (3호 — 계획)

> 선정 이유: **전 관절 토크센서** 탑재 → `HasJointTorqueSensor=true`인 최고 품질 데이터. Pro 등급 쇼케이스 벤더.

- [ ] 사전 조사: xCore 컨트롤러 외부 통신 옵션 (SDK/RCI, 표준 프로토콜 지원 여부)
- [ ] `Drivers.Rokae` 구현 → §6 온보딩 체크리스트

### 5.4 후보 풀 (우선순위 미정 — 시장 요구 발생 시 §6 절차로 착수)

- **Universal Robots**: RTDE(125/500Hz, 읽기 전용 스트림 — 비개입 원칙과 정합성 최상, 문서 공개). 시장 점유율 1위라 영업 요구 발생 가능성 높음.
- **Techman(TM)**: Modbus TCP 맵 공개, Ethernet Slave 데이터 스트림.
- **기타**(FANUC CRX, ABB GoFa 등): 요구 발생 시 조사.

---

## 6. 신규 벤더 온보딩 절차 (표준 체크리스트)

> 두산 온보딩 경험을 절차화한 것. 새 벤더는 이 순서를 따른다. 목표 기간: 벤더당 조사 1주 + 구현 2주 + 실기 검증 1주.

1. [ ] **문서 조사**: 컨트롤러의 외부 통신 채널 목록화 (표준 프로토콜 / 네이티브 SDK / 실시간 스트림). 공식 레지스터 맵·SDK 문서 확보.
2. [ ] **채널 분류**: 범용(Basic) 채널과 네이티브(Pro) 채널 선정. 각 채널의 패시브 여부(`IsPassive`) 판정 — **읽기 전용 보장이 안 되는 채널은 라이브 경로 사용 금지.**
3. [ ] **프로브 콘솔 제작**: `tools/{Vendor}Probe` — 연결·데이터 덤프·diff 관찰 (두산 `ModbusScan`/`DrflProbe` 패턴 재사용).
4. [ ] **실기 매핑/검증**: 조그 격리로 신호-축 대응 확정, 스케일 교정(펜던트 표시값 대조). 미확정 스케일은 `VendorRaw`로.
5. [ ] **드라이버 구현**: `Drivers.{Vendor}` — `IRobotTelemetrySource` 구현 + `RobotCapabilities` 선언 + 단위 정규화.
6. [ ] **계약 준수 테스트**: 공용 프레임 유닛테스트(단위·축 인덱스·널 규약) + 등급 자동 판정 확인.
7. [ ] **파이프라인 통과 확인**: 수집 → TimescaleDB 적재 → 대시보드 표시가 **코어 코드 수정 없이** 동작하면 온보딩 완료. (코어 수정이 필요했다면 §3 벤더 격리 원칙 위반 — 계약을 보강하고 재시도.)
8. [ ] 본 문서 §5에 모듈 섹션 추가, 실측 맵·버전 호환성 노트 기록.

---

## 7. 확인 필요 사항 (Open Questions)

**플랫폼 공통**
- [ ] Collector 분리 시 코어/드라이버의 `net8.0-windows` 의존 제거 가능 범위 (DRFL P/Invoke는 Windows DLL — 드라이버만 windows 타깃, 코어는 중립 유지가 목표)
- [ ] 벤더별 샘플링 주기 편차(1~100Hz+)의 저장·피처 리샘플링 규약

**두산**
- [x] CS-01 DRCF 버전 — **V2.11.1**(`GV02110100`) 확정 → DRFL v2 브랜치(`DRCF_VERSION=2`), RT API(UDP) 지원(2.8+).
- [x] Modbus에 축별 전류·토크 노출 여부 — **노출됨**(400~405) → Basic 등급 Modbus만으로 성립.
- [x] DRFL 구조체 필드 — `DRFS.h` 실측 확인 (§5.1 참조).
- [x] Modbus UnitId — **1**(및 255). FC04 미지원, **FC03만** 사용.
- [ ] Modbus 온도·전류/토크 스케일 확정 (조그하며 비영 값 확보 후 펜던트 대조)
- [ ] DRCF 2.11 매칭 v2 DRFL DLL 확보 (§5.1 검증 노트의 입수 경로 ⓐⓑ)

---

## 8. 작업 컨벤션 (Claude Code 참고)

- 빌드/실행: `BODA.CMS.sln`을 VS2022에서 열고 NuGet 복원 후 F5. 테스트: `dotnet test tests/BODA.CMS.Tests`.
- 아키텍처: MVVM 유지. 벤더 통신은 `Drivers/{Vendor}`에, 벤더 중립 인프라(Modbus 세션 등)는 `Services`에, 계약은 `Core/Telemetry`에, 화면 상태는 `ViewModels`에. 코드비하인드는 컴포지션 루트(`MainWindow.xaml.cs`) 역할만.
- **로봇 쪽에 프로그램을 다운로드하지 않는다**(무코딩 원칙). 모든 통신은 PC 측에서 컨트롤러로.
- **생산 작업을 방해하지 않는다**(비개입 원칙). 라이브 경로는 패시브 채널만.
- **벤더 코드를 격리한다**: 새 벤더 지원은 드라이버 모듈 추가로만. 코어·UI·저장 계층에 벤더 분기 금지.
- 새 벤더 착수 시 §6 온보딩 체크리스트를 복사해 §5에 모듈 섹션으로 추가.

---

## 9. 참고 자료

**플랫폼 공통**
- NModbus: NuGet `NModbus` (3.x)
- Modbus 주소 규칙: 4xxxx 표기의 `40001` = NModbus의 0-based 주소 `0`

**두산**
- DRFL (오픈소스): GitHub `DoosanRobotics/API-DRFL`, `DoosanRobotics/doosan-robot` (`common/include/DRFL.h`)
- 두산 API/매뉴얼: robotlab.doosanrobotics.com, manual.doosanrobotics.com
- 주요 접속 정보: 기본 컨트롤러 IP `192.168.137.100` (현장 실기 `192.168.1.100`) / DRFL 포트 `12345` / RT 제어 포트 `12348` / Modbus TCP `502`
- DRCF 버전 호환: `DRFL.h`의 `DRCF_VERSION` 매크로(v2/v3)로 분기. **본 장비는 v2 → `DRCF_VERSION=2`.**
- 버전 표기 해석: 티치펜던트 패키지 `GV0X YY ZZ WW` = **V X.YY.ZZ** (예: `GV02080000`=V2.8.0, `GV02110100`=V2.11.1). 프리픽스 `GV`=v2 계열, `GF`/`03xx`=v3 계열.

**JAKA / Rokae / UR** (조사 시 채움)
- JAKA: 공식 SDK·Modbus 문서 URL
- Rokae: xCore 외부 통신 문서 URL
- UR: RTDE Guide (universal-robots.com 개발자 문서)
