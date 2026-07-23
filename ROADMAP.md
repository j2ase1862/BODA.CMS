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
| `BODA.CMS.Comms` | 벤더 중립 프로토콜 헬퍼(Modbus TCP 세션 유지 등) — 여러 드라이버가 공유 | 없음 |
| `BODA.CMS.Drivers.Doosan` | Modbus 맵 + 스케일, DRFL P/Invoke, 두산 프레임 → 공용 프레임 변환 | 두산 |
| `BODA.CMS.Drivers.{Vendor}` | (벤더 추가 시) 해당 벤더 채널 + 변환 | 해당 벤더 |
| `BODA.CMS.Collector` | headless 수집 서비스: 드라이버 로드, 버퍼링, TimescaleDB 적재, 재연결 | 없음 |
| `BODA.CMS.Analytics` | CBM 기준선·임계값, ONNX 추론 | 없음 |
| `BODA.CMS` (WPF) | 모니터링 UI. Collector/WebAPI를 구독하는 뷰어 | 없음 |

> **물리 분리 완료(P1)**: `BODA.CMS.Core`(net8.0) / `BODA.CMS.Comms`(벤더 중립 Modbus 헬퍼, NModbus) / `BODA.CMS.Drivers.Doosan` / `BODA.CMS.Drivers.Simulated` / `BODA.CMS.Collector`(Worker) / `BODA.CMS`(WPF, net8.0-windows) / `tests`. Windows 의존은 WPF 앱뿐 — 코어·드라이버·수집기는 net8.0 중립. 벤더 드라이버 타입은 컴포지션 루트(`MainWindow.xaml.cs`, Collector `Program.cs`)에서만 등장한다.

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

### Phase P1 — 데이터 파이프라인 & 저장 + 수집기 분리 ✅ 코드 완료 (2026-07) — 실 DB 검증 대기
- [x] 프로젝트 물리 분리: Core / Comms / Drivers.Doosan / Drivers.Simulated / Collector (§2 모듈 표 실현, DrflProbe도 모듈 참조로 전환)
- [x] TimescaleDB 스키마(hypertable): `telemetry_frames` — robot_id/vendor/channel 태그 + 축별 `real[]` + `vendor_raw jsonb`, 바이너리 COPY 배치 적재. **벤더별 테이블 없음.** TimescaleDB 확장 미설치 시 일반 테이블 폴백.
- [x] `BODA.CMS.Collector` headless 수집 서비스 (Worker Service): 벤더 카탈로그 → 채널별 펌프 → bounded 버퍼(포화 시 DropOldest — 수집이 저장을 기다리지 않음) → 배치 적재. `Storage:Enabled=false`면 dry-run(수집률 로그).
- [x] 연결 끊김·재연결 복구: 계약에 재연결 시맨틱 명시(Faulted 후 ConnectAsync 재호출 가능), 드라이버 3종 보장 수정(두산 Modbus 연속오류 20회→Faulted·세션 소유 모드, DRFL 핸들 선정리), 지수 백오프 1s→30s 상한.
- [x] 다중 로봇 동시 수집: `appsettings.json Collector:Robots[]` (RobotId·Vendor·Host·Port·Channels 필터) — 시뮬레이터 2채널로 end-to-end 검증.
- [ ] 실 TimescaleDB 인스턴스 대상 적재 검증 (개발 PC에 5432 부재 — dry-run으로 파이프라인만 검증됨)
- [x] ~~WPF를 Collector 구독 뷰어로 전환~~ → **웹 대시보드가 원격 다중 로봇 뷰어 역할 수행(P5)**. WPF는 현장 직결 진단 도구로 역할 분리 — 전환 불필요 판단.

### Phase P2 — 조건기반 감시(CBM) ✅ 1차 완료 (2026-07)
- [x] **기준선 학습**: `BODA.CMS.Analytics.CbmMonitor` — 1초 집계 → 신호·축별 Welford 평균/σ(기본 60집계 ≈ 1분). 위치/속도는 동작 의존이라 감시 제외, **VendorRaw 원시 신호도 감시**(스케일 미확정인 Basic 채널도 CBM 성립). σ 하한(절대 1e-3 + 상대 1%)으로 상수 신호 과민 알람 방지.
- [x] **편차·추세 임계값 알림**: 급변(즉시값 z≥4, 디바운스 3회)=Alarm · 드리프트(EWMA z≥3, 디바운스 10회)=Warning · 정상 복귀 시 Info 해제 통지. 채널당 `CbmMonitor` 1개, 공용 프레임만 소비(벤더 무관).
- [x] **WPF 대시보드 1차**: 카드 건강도 칩(최악 z 기반 0~100 휴리스틱) + CBM 알림 리스트 카드 + 축별 라이브 차트(기존). 시뮬레이터 **결함 주입**(t=100s, J3 토크/전류 +35%·온도 +6℃ 램프)으로 학습→알람→식별(J3) 경로 검증.
- [x] 다중 로봇 동시 뷰 — **P5 웹 대시보드로 구현** (채널 카드 그리드 + 통합 알림 스트림)
- [x] Collector에 CBM 통합 + 알림 DB 저장(`telemetry_alerts`) — **P5에서 구현** (통보 채널(메일/메신저)은 잔여)
- [ ] 알림 통보 채널 (메일/메신저 webhook) — 고객 요구 시
- [ ] 기준선 영속화(재시작 시 재학습 생략)·운전 조건별(프로그램/모드) 기준선 분리 — 실 데이터 확보 후

### Phase P3 — ML 이상탐지 ✅ 1차 완료 (2026-07, 부트스트랩 모델)
- [x] 피처 엔지니어링 + 리샘플링 규약: CBM 1초 집계의 **z-정규화 값** 슬라이딩 윈도(10) → 6피처(mean/std/rms/min/max/slope). z-공간 입력이라 신호·벤더·샘플링 주기(1~100Hz) 무관 — **단일 모델이 전 채널 커버**. 정의는 C#(`AnomalyFeatures`)·Python(train 스크립트) 양쪽 동일 유지 필수.
- [x] Python 학습 → ONNX: `tools/ml/train_anomaly.py` — IsolationForest(비지도) → skl2onnx export + 임계값 사이드카(json, 정상 0.5퍼센타일 캘리브레이션). sklearn↔ONNX 점수 정합 검증 포함. **현재 모델은 합성 정상(z-공간) 학습 부트스트랩** — 실 데이터 축적 후 같은 스크립트로 재학습해 `models/`만 교체.
- [x] .NET ONNX Runtime 추론: `Analytics/Ml/OnnxAnomalyScorer` + `MlAnomalyMonitor`(CBM 집계 스트림 구독, 디바운스·자동 해제). 모델 파일 없으면 ML만 꺼진 채 동작.
- [x] UI/알림 연동: 카드 ML 칩("ML 정상"/"ML 이상 n건") + CBM 알림 리스트 공유(Kind "ML 이상"/"ML 복귀").
- [ ] 실 데이터 재학습 파이프라인: TimescaleDB → 피처 추출 → 학습 (P1 실 DB 검증 이후)
- [x] Collector에 ML 통합(무인 감시 경로) — **P5에서 구현** (채널당 CBM+ML, 웹 대시보드 노출)

### Phase P4 — 비전 진단 (바심 차별화, 벤더 무관) ✅ PoC 완료 (2026-07, 합성 씬)
- [x] **반복정밀도 드리프트·마모 진단 PoC**: `BODA.CMS.Vision` — Otsu+최대블롭 서브픽셀 마커 검출기(합성 씬 실측: 평균 오차 0.03px ≈ 1.5µm@0.05mm/px, 대비 검증으로 무마커 오검출 차단) + `RepeatabilityMonitor`(기준선 학습 → 위치 드리프트 Alarm·지름 감소 마모 Warning — P2와 같은 z-판정). 데모: `tools/VisionPoc` — 드리프트 주입 후 이탈 0.022mm 시점(3~4사이클)에 알람.
- [x] 센서·비전 **알림 레벨 융합**: 비전 알림도 `CbmAlert`로 발화 — 텔레메트리 CBM/ML과 같은 알림 채널·대시보드에 합류.
- [ ] 실 카메라 연동: `IMarkerImageSource` 추상화 + 캡처 SDK + px→mm 캘리브레이션 절차 (카메라 확보 후 — 합성 씬과 검출기·감시기는 그대로 재사용)
- [ ] 피처 레벨 융합(센서+비전 조인트 진단·상호 검증) — 실 데이터 축적 후

### Phase P5 — 제품화 ✅ 1차 완료 (2026-07)
- [x] **구독/라이선스 모델**: RSA-SHA256 서명 라이선스 파일(`license.json`) — `Core.Licensing.LicenseVerifier`. 파일 없음=평가판(전체 기능), 서명 불량/만료=Basic 강등, 정식=등급대로. **등급 게이팅은 capability 자동 판정(§1)과 연동** — WPF(카드 시작 차단)·Collector(채널 미기동) 양쪽 강제. 발급 도구 `tools/LicGen`(init/issue). ⚠️ `dev-keys/`는 개발 서명키 — 운영 발급 키는 저장소 밖 보안 저장소에서 관리하고 Core 공개키 상수 교체.
- [x] **웹 대시보드**: Collector가 ASP.NET Core 호스트로 승격 — `/api/status`(채널 상태·수집률·CBM/ML 스냅샷)·`/api/alerts` REST + 다크 테마 정적 대시보드(1초 폴링, http://localhost:5100). 다중 로봇·다중 사용자 원격 모니터링. (Blazor/SignalR 스트리밍·BODA.VMS.Web 자산 통합은 후속 — 현 대시보드는 폴링 기반으로 의존성 제로.)
- [x] **Collector 무인 감시** (P2/P3 잔여 통합): 채널당 CBM+ML 부착, 알림은 대시보드 링(200건) + `telemetry_alerts` 테이블 저장(Storage on 시).
- [x] **패키징**: `tools/package.ps1` — WPF 앱·Collector를 win-x64 self-contained publish 후 **MSI(WiX 5, `installer/*.wxs`)** + 보조 zip. Collector MSI는 서비스 등록·시작·장애 재시작까지 처리(C:\BODA\Collector, appsettings는 NeverOverwrite로 업그레이드에도 보존), 앱 MSI는 시작 메뉴·바탕화면 바로가기. 현장 PC .NET 설치 불필요, 라이선스는 패키지 미포함·고객별 발급. 실측: app msi 67.6MB / collector msi 43.4MB. ⚠ WiX는 5.0.x 고정 — 6+는 OSMF 동의 필요.
- [x] **통합 설치 번들**: `installer/Bundle.wxs`(Burn) — `collector-setup-{v}-x64.exe`(396MB, PostgreSQL 16 동봉·오프라인 단일 파일). 기설치 PG는 레지스트리 감지로 건너뜀. DB·테이블은 Collector가 첫 시작 때 자가 생성(`EnsureDatabaseAsync`, 3D000→CREATE DATABASE) + 저장소 초기화 실패는 백오프 재시도(StopHost 방지). 원격 DB·기존 PG 재사용은 `tools/install-db.ps1`.
- [ ] 자동 업데이트 채널·드라이버 모듈 단위 배포 — 고객 배포 시점에
- [ ] Blazor/SignalR 실시간 대시보드 + 사용자 인증 — 다중 고객 SaaS화 시점에

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

### 5.2 JAKA (2호 — 드라이버 구현 완료, 실기 검증 대기)

> 선정 이유: 인터페이스가 가장 개방적, Modbus 기본 ON → Basic 채널 온보딩 비용 최소 예상.

**채널 구성**
- **네이티브 모니터 스트림(구현 완료)**: TCP 10000 — 컨트롤러가 상태 JSON을 ~10Hz 브로드캐스트, 드라이버는 **수신만**(명령 포트 10001 미사용 → 구조적 비개입·무코딩, `IsPassive=true`).
- **Modbus TCP(잔여)**: 기본 ON이나 레지스터 맵은 실기 프로브로 검증 후 추가 — 추측 주소로 구현하지 않음(두산과 동일 절차).

**진행 상태 (§6 체크리스트 기준)**
- [x] 1. 문서 조사 — 채널 목록화: 모니터 스트림(10000)·명령(10001)·Modbus(502)
- [x] 2. 채널 분류 — 모니터 스트림=패시브 확정(수신 전용). 등급은 capability 규약대로 실기 확정 전 **Basic**(전류/토크/온도가 VendorRaw 단계 → `Has*=false`)
- [x] 3. 프로브 콘솔 — `tools/JakaProbe`: 스트림 덤프 + 패킷 주기 실측 + 숫자 배열 필드(신호 후보) 열거 + 파서 통과 확인
- [x] 5. 드라이버 구현 — `Drivers.Jaka`: 브레이스 프레이머(TCP 분할/병합·문자열 내 중괄호 안전), 방어적 파서(펌웨어별 필드 차이 대응 — `instCurrent`/`joint_temp`/`torqsensor` 등 후보를 VendorRaw로 보존, `data` 래핑 대응), 재연결 시맨틱(무수신 5s/원격 종료 → Faulted)
- [x] 6. 계약 준수 테스트 — 가짜 컨트롤러 TCP 서버로 접속→프레임→Faulted 전이 검증 + 파서/프레이머/등급 유닛테스트 6건
- [x] 7. 파이프라인 통과 — **코어·UI·수집기 수정 없이** 카탈로그 등록만으로 WPF·Collector에 노출(벤더 격리 원칙 준수 확인)
- [ ] 4. **실기 매핑/검증(하드웨어 필요)**: JakaProbe로 ⓐ 스트림 주기 실측 ⓑ `joint_actual_position` 단위 대조(도° 여부 — 펜던트 비교) ⓒ 전류/온도/토크 필드명·단위 확정 → 정규화 매핑 + capability 상향(Pro 승격 검토) ⓓ Modbus 레지스터 맵 검증 → `JakaModbusSource` 추가
- [ ] 8. 실측 맵·펌웨어 호환성 노트 기록 (실기 후)

### 5.3 Rokae (3호 — 드라이버 본체 완료, SDK 바이너리·실기 대기)

> 선정 이유: **전 관절 토크센서** 탑재 → `HasJointTorqueSensor=true`인 최고 품질 데이터. Pro 등급 쇼케이스 벤더.

**조사 결과 (2026-07, 웹 리서치 — §6 1·2단계)**
- 공식 SDK 공개: `github.com/RokaeRobot` — xCoreSDK-CPP/Python/**CSharp**/Android, **Apache-2.0**. C# SDK는 C++/CLI 래퍼(`xCoreSDK_cli.dll`, x64 Windows, .NET≥5, NuGet 없음 — Releases zip 수동 취득).
- **본선 채널 = SDK 비실시간 상태 조회**(위치·상태 읽기, 모션 명령 미사용) — C# SDK는 비실시간 전용이라 제어권 이슈 자체가 없음. 폴링 = 패시브(두산 Modbus 폴링과 동일 논리).
- **와이어 프로토콜 비공개**(프리빌트 라이브러리) → 순수 C# 재구현 불가 — SDK 바이너리 번들 필요(두산 DRFL 전례, `libs/rokae/`).
- RCI 1kHz 실시간 모드 = 제어 장악 → **오프라인 캐릭터라이제이션 전용**(두산 RT와 동일 분류).
- Modbus: 문서상 확장 IO 용도만 확인 — 텔레메트리 레지스터 맵 미확인 → 채널 후보 보류.

**진행 상태 (§6 체크리스트 기준)**
- [x] 1·2. 문서 조사·채널 분류 (위 조사 결과)
- [x] 5. 드라이버 본체 — `Drivers.Rokae`: `RokaeXCoreSource`(10Hz 폴링, 전 신호 정규화 매핑, 연속 실패 10회→Faulted, 재연결 시맨틱) + `IRokaeStateClient` 추상화(통신부 — SDK 확보 시 `XCoreSdkStateClient` 구현체만 추가). capability: 전축 JTS·전류·온도 true → **Pro 자동 판정**.
- [x] 6. 계약 준수 테스트 — 가짜 상태 클라이언트로 폴링·정규화·Faulted·재연결 검증 4건
- [ ] **SDK 바이너리 확보**: xCoreSDK-CSharp Releases zip → `libs/rokae/` 번들 → `XCoreSdkStateClient` 구현 → 카탈로그 등록 (미구현 클라이언트 상태로는 콤보에 노출하지 않음 — 빈 껍데기 금지)
- [ ] 3·4. 프로브·실기 매핑(하드웨어 필요): 상태 조회 주기 상한 실측, 단위 대조(SDK 라디안 여부), 토크센서 값 sanity → capability 검증
- [ ] 8. 실측·펌웨어 호환성 노트 기록 (실기 후)

### 5.4 후보 풀 (우선순위 미정 — 시장 요구 발생 시 §6 절차로 착수)

- ~~Universal Robots~~ → §5.5로 승격 (2026-07 드라이버 구현).
- **Techman(TM)**: Modbus TCP 맵 공개, Ethernet Slave 데이터 스트림.
- **기타**(FANUC CRX, ABB GoFa 등): 요구 발생 시 조사.

### 5.5 Universal Robots (4호 — 실기 연결·연속 수신 확인, 세부 대조 잔여)

> 선정 이유: 시장 점유율 1위 + RTDE가 **문서 공개 바이너리 프로토콜**(TCP 30004, 출력 구독 수신 전용)이라
> 비개입 원칙과 정합성 최상. 외부 SDK·네이티브 DLL 불필요 — 순수 C# 구현(두산 DRFL·Rokae SDK 번들 전례와 대비).

**조사 결과 (2026-07 — §6 1·2단계)**
- 본선 채널 = **RTDE 출력 구독**: 프로토콜 v2(레시피 id + 출력 주기 지정, URControl 3.10+/5.4+ 필요).
  컨트롤러가 구독 변수만 주기 송신 — 입력 레시피·스크립트 전송 미사용 → 구조적 비개입·무코딩.
- 샘플 주기: CB3 125Hz / e-Series 500Hz — 드라이버는 125Hz 요청(실기에서 상향 검토).
- 신호 매핑: `actual_q`(rad→°), `actual_qd`(rad/s→°/s), `actual_current`(A), `target_moment`(Nm, 모델 목표 토크),
  `joint_temperatures`(℃). **관절 토크센서 미탑재**(e-Series는 TCP F/T 센서만) → `JointTorqueNm` 없음(null 규약).
- 30003 실시간 클라이언트·29999 대시보드 서버 등 다른 포트는 불필요 — RTDE 하나로 충분.

**진행 상태 (§6 체크리스트 기준)**
- [x] 1·2. 문서 조사·채널 분류 (위 조사 결과)
- [x] 5. 드라이버 본체 — `Drivers.UR`: `UrRtdeSource`(버전 협상→레시피 구독→시작, 무수신 5s→Faulted, 재연결 시맨틱)
      + `RtdeProtocol`/`RtdeFramer`(패키지 조립·해석·경계 복원). capability: 전류·온도 true → **Pro 자동 판정**
      (RTDE 문서가 단위를 명세 — Rokae 전례. 실기 검증 게이트는 아래 잔여).
- [x] 6. 계약 준수 테스트 — 가짜 컨트롤러 TCP 서버로 핸드셰이크·rad→° 정규화·NOT_FOUND 거부·Faulted 검증
- [x] 카탈로그 등록 — WPF 앱 + Collector ("ur") — 코어 수정 없음(§3 격리 확인)
- [x] ~~URSim 파이프라인 통과~~ → 실기 검증으로 대체 (2026-07-24 실기 연결, 수 시간 연속 수신·CBM 동작 확인)
- [ ] 🔶 3·4. 실기 매핑·검증 잔여: 실 주기 확인(e-Series 500Hz 상향 검토), `actual_current` 부호·단위 sanity, `target_moment` 대조
- [ ] 8. 실측·펌웨어 호환성 노트 기록 (실기 후)

**실기 발견 (2026-07-24)**: 수 시간 연속 가동 시 관절 온도 웜업이 CBM 건강도를 0까지 끌어내림 —
온도는 기동 후 수십 ℃ 상승이 정상 거동이라 기준선 z 판정이 구조적으로 부적합(σ 하한 탓에 z 폭주).
→ **온도 신호만 절대 임계 판정으로 전환**(경고 60℃/알람 75℃ 기본, `Collector:Cbm:TemperatureWarn/AlarmCelsius` 설정):
경고 아래 건강도 영향 0, 경고~알람 선형 감점, 알람 지속 시 "과열" Alarm. 순간 블립은 기존 디바운스로 무시.

**작업 노트**: 이 온보딩과 함께 **채널 등급 수동 선택**(WPF 카드 '등급' 콤보) 추가 — 자동 판정을 넘는 상향은 불가,
하향 선택 시 `TierSignalFilter`(Core)가 심층 신호(전류·토크·온도)를 프레임 단계에서 차단해 라이선스 게이팅 우회를
막는다. Basic 라이선스 현장에서 Pro급 채널(UR 등)을 위치·속도만으로 운용하는 영업 시나리오 대응.

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
- [x] Collector 분리 시 `net8.0-windows` 의존 제거 범위 — **전부 제거 가능 확인**: Core·Comms·드라이버·Collector 모두 net8.0 중립 (P/Invoke 선언은 컴파일 타깃 무관, DRFL 채널만 런타임 win-x64 전제). windows 타깃은 WPF 앱뿐.
- [x] 벤더별 샘플링 주기 편차(1~100Hz+)의 피처 리샘플링 규약 — **해소(P3)**: 1초 집계 + 기준선 z-정규화 공간에서 피처 추출 → 주기·스케일 무관. (저장은 원 주기 그대로 — P1 스키마.)

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

**JAKA / Rokae** (조사 시 채움)
- JAKA: 공식 SDK·Modbus 문서 URL
- Rokae: xCore 외부 통신 문서 URL

**UR**
- RTDE 사양: Universal Robots "Real-Time Data Exchange (RTDE) Guide" (universal-robots.com 개발자 문서) — 프로토콜 v2
- URSim 시뮬레이터: Docker Hub `universalrobots/ursim_e-series` — RTDE를 실기와 동일하게 서빙(하드웨어 없이 검증 가능)
- 주요 포트: RTDE **30004** / 대시보드 29999 / 실시간 스트림 30003 — 본 드라이버는 30004만 사용
- 출력 변수 레퍼런스: RTDE Guide 의 "field names" 표 (`actual_q` 등 — 단위·타입 명세)
