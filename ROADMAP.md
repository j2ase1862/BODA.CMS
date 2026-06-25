# 두산 협동로봇 상태 진단 키트 — 개발 로드맵

> 이 문서는 프로젝트의 단일 기준(source of truth)입니다. 이 저장소에서 작업하는 어시스턴트(Claude Code 등)는 먼저 이 문서를 읽고, 작업 완료 시 해당 체크박스를 갱신하세요.

---

## 1. 프로젝트 개요

- **무엇**: 협동로봇의 상태를 비개입 방식으로 모니터링하고, 단계적으로 예지보전(Predictive Maintenance)까지 확장하는 솔루션.
- **목적**: 대량이 아닌 **소량이라도 지속 가능한(구독형) 매출** 창출.
- **차별화**: 단순 센서 키트가 아니라 ① 라인 운영 통합 + ② 머신비전 기반 진단(바심의 핵심 역량)을 결합. 핵심 가치는 "하드웨어"가 아니라 "통합 + 소프트웨어 + 구독 서비스".
- **제품 등급(목표)**:
  - **Basic** — Modbus 폴링 기반 모니터링 (전 기종 무코딩 커버)
  - **Pro** — 제조사 네이티브 API 기반 심층 진단(축별 전류·토크)
  - **Plus** — 외부 진동 센서(kHz FFT) + 비전 진단

---

## 2. 기술 스택 & 아키텍처

**스택**
- 플랫폼: .NET 8 (`net8.0-windows`)
- UI: WPF (MVVM)
- Modbus: NModbus 3.x
- 두산 네이티브: DRFL (Doosan Robot Framework Library, 오픈소스)
- 시계열 저장: TimescaleDB (기존 BODA 스택)
- AI: Python 학습 → **ONNX** export → .NET ONNX Runtime 추론 (C# 영역 안에서 처리)

**데이터 흐름 (아래에서 위로)**
```
현장 설비 (PLC · 관절 센서: 토크/전류/온도)
   ↓ 수집
DRFL 모니터링 콜백 / Modbus 폴링
   ↓ 저장
TimescaleDB (축별 시계열)
   ↓ 지능
AI 계층 (이상탐지 · RUL) — Python 학습 / ONNX 추론
   ↓ 서비스
.NET WebAPI · SignalR
   ↓ 표현
WPF 모니터링 UI (+ 향후 3D 트윈)
```

---

## 3. 핵심 결정사항 & 제약 (반드시 준수)

- **타깃 하드웨어**: 두산 **H2017** / 컨트롤러 **CS-01** / **DRCF V2.11.1**(패키지 `GV02110100`, v2 라인). → DRFL 빌드는 `DRCF_VERSION=2`.
- **데이터 채널 우선순위**: **DRFL이 메인**, Modbus는 베이스라인/연결 프로브용.
  - 두산 Modbus는 마스터 중심이라 축별 전류·토크가 안 나올 가능성이 높음. CS 컨트롤러의 "Modbus Server Data" 테이블은 주로 상태·위치·속도 노출.
  - 축별 전류·토크 등 진단 핵심 데이터는 DRFL에서 가져온다.
- **⚠️ 비개입 원칙 (가장 중요)**: 이 솔루션은 **로봇의 실제 생산 작업을 절대 방해하면 안 된다.**
  - 라이브 모니터링 → DRFL **패시브 모니터링 콜백**(`_SetOnMonitoringData`) 사용. 로봇이 자기 프로그램을 돌리는 동안 상태만 수동 수신.
  - DRFL **RT 제어 모드**(`connect_rt_control`, 1kHz)는 로봇을 장악하므로 **라이브 감시에 쓰지 말 것**. 건강한 로봇의 기준 시그니처를 정밀 측정하는 **오프라인 캐릭터라이제이션 용도로만** 사용.
- **멀티벤더 로드맵**: 두산(보유) → JAKA(인터페이스 가장 개방적, Modbus 기본 ON) → Rokae(전 관절 토크센서, 최고 데이터). **벤더별 드라이버 추상화 계층** 필요 — 한 맵을 전 기종에 재사용 불가.
- **PdM 성숙도 사다리 (순서대로 올라간다)**:
  1. 모니터링 (descriptive)
  2. 조건기반 감시 CBM (임계값·추세, ML 불필요) ← 현장 가치의 대부분
  3. ML 이상탐지 (비지도, 고장 라벨 불필요)
  4. 잔여수명 RUL 예측 (고장 이력 충분히 축적 후) ← 대부분 3단계까지가 현실
- **언어/배포**: C# 자산 최대 활용. 로봇 쪽에는 단 한 줄의 프로그램도 다운로드하지 않는 "무코딩 + 완전 패키지화"를 유지.

---

## 4. 현재 상태

- [x] **Phase 0 — WPF 스켈레톤 + 연결 테스트** (`DoosanMonitor` 솔루션 생성 완료)
  - MVVM 구조: `ViewModelBase`, `AsyncRelayCommand`
  - `Services/ModbusConnectionService` — TCP/Modbus 연결을 **유지**(이후 폴링·모니터링에서 재사용)
  - `MainWindow` — IP/포트 입력, 연결 버튼, 상태등(회색/주황/초록/빨강), 로그
  - 연결 시 홀딩 레지스터 40001~ 시험 읽기
- [~] **Phase 1 — Modbus 프로브 & 맵 + 앱 통합** — 실기 매핑 완료, **앱 기능화 완료**(스케일 보정만 남음).
  - 매핑: `tools/ModbusScan` 콘솔로 unit=1·FC03 확인, J1 조그 격리로 **위치(270~275)·온도(300~305)·전류/토크(400~405)** 블록 확정. 위치 **0.1°/count 교정**. FC04 미지원.
  - 통합: `Services/ModbusTelemetryService`(연결 재사용, 10Hz 폴링) + `Models/ModbusTelemetrySample` + MainWindow "Modbus 텔레메트리" 카드(라이브 축별 표). **결정 게이트 통과: Basic 등급 Modbus만으로 동작.**
  - ✅ **실기 검증 완료**: 실제 로봇(192.168.1.100)에서 앱 라이브 폴링 동작, J1 조그 시 위치 실시간 추종 확인.
- [~] **Phase 2 — DRFL 연동(패시브 모니터링)** — 코드 완료, **실기 검증 대기**(DRCF 2.11 매칭 DLL 필요 — 위 Phase 2 노트 참조).
  - `Services/Drfl/` — `DrflStructs.cs`(DRFS.h Pack=1 미러), `DrflInterop.cs`(`DRFLWin64.dll` P/Invoke, `__cdecl`).
  - `Services/DrflMonitorService.cs` — `_SetOnMonitoringData` 패시브 콜백 → `MonitoringSample`(축별 위치/토크/전류/온도). **비개입**(명령 권한 미취득).
  - `MainWindow` — DRFL 모니터링 카드(상태등 + 라이브 축별 표).
  - `libs/x64/` — DRFL+Poco 네이티브 DLL 번들, 빌드 시 자동 복사(로드/심볼 검증 완료).
  - 남은 것: **실 컨트롤러 연결 테스트**(콜백 수신·축별 값 sanity·`GetSystemVersion` 정합)만 하드웨어 필요.

---

## 5. 확인 필요 사항 (Open Questions — 작업 전 해소)

- [x] **CS-01 DRCF(소프트웨어) 버전** — 티치펜던트 패키지 버전 `GV02110100` = **DRCF V2.11.1 (v2 라인)** 확정. → DRFL는 **v2 브랜치(`DRCF_VERSION=2`)**로 빌드, RT API(UDP)도 지원됨(2.8+ 에서 동작 확인).
- [ ] **Modbus에 축별 전류·토크가 노출되는가?** — Phase 1 프로브로 실측. 없으면 곧장 DRFL로.
- [x] **DRFL 구조체 필드** — `DRFS.h` 실측 확인. 진단 데이터는 `MONITORING_DATA`(non-EX)에 전부 존재: 토크 `_tCtrl._tTorque`(JTS/동역학/외부), 전류 `_tMisc._fActualMC`, 온도 `_tMisc._fActualMT`. EX는 world/user 좌표만 추가 → **non-EX 채택**. 헤더는 `refs/`에 보관.
- [x] **Modbus UnitId** — 실측: **1**(및 255)이 응답. 0은 무응답. **FC04(입력 레지스터) 미지원**(ILLEGAL FUNCTION), **FC03(홀딩)만** 사용.
- [ ] **Modbus float 워드 오더** — ABCD vs CDAB. (정지 상태라 레지스터가 거의 0 → 조그하며 비영 값 확보 후 판정.)

---

## 6. 단계별 작업 (체크리스트)

### Phase 1 — Modbus 프로브 & 맵 발견
> 목표: 링크 검증 + Modbus에 무엇이 노출되는지 확인 → DRFL 전환 여부 결정 게이트.

- [x] 연결 테스트로 TCP 그린 라이트 확인 — `tools/ModbusScan` 콘솔로 실측(unit=1, FC03).
- [x] 레지스터 덤프 뷰 — `tools/ModbusScan`: 홀딩 0~511 청크 폴링 + 직전 스냅샷 자동 diff.
- [x] 로봇을 조그하며 변하는 레지스터 관찰 — J1 소/대 조그로 격리. **270만 J1 따라 변함**(271~275 불변) → 위치 블록 확정.
- [x] CS "Modbus Server Data" 주소 맵 작성 — 아래 표. (정밀 단위/스케일은 펜던트 표시값과 대조 필요.)
- [x] float 워드 오더 — **해당 없음**: 텔레메트리(위치/온도/전류)가 **단일 int16 스케일값**으로 노출(2워드 float 아님). 워드오더 이슈 소멸.
- [x] **결정 게이트 통과**: **전류/토크가 Modbus(400~405)에 존재** → **Basic 등급을 Modbus만으로 구현 가능**(DRFL 없이 축별 위치+전류/토크+온도 확보). DRFL(Pro)은 고분해능·축별 토크센서용으로 병행.

> **Modbus 홀딩 레지스터 맵 (CS-01, DRCF V2.11.1, unit=1, FC03, 0-based)** — `tools/ModbusScan` 실측:
> | 주소(0-based / 4xxxx) | 내용 | 형식 | 비고 |
> |---|---|---|---|
> | 270~275 / 40271~40276 | **축별 위치 J1~J6** | int16, **×0.1 = °** | 270=J1 격리 확정. **스케일 0.1°/count 확정**(270=-687 ↔ 펜던트 -68.70°) |
> | 300~305 / 40301~40306 | **축별 온도(℃)** | int16, ~1℃/count(추정) | 정지 시 일제히 냉각 드리프트로 확인 |
> | 400~405 / 40401~40406 | **축별 전류/토크** | int16 | 자세/중력부하 의존, 동적. 스케일 미확정 |
> | 256~260 / 40257~40261 | 상태/모드 추정 | int16 | 259가 카운터성 변동 |
> | 290~294 | 미확정(좌표/속도 후보) | — | J1 회전 시 소폭 변동 |
> | 450~479 | 이벤트/큐 버퍼(텔레메트리 아님) | — | 묶음이 0으로 빠짐 → 제외 |
> - 미확정: 온도·전류/토크 스케일, 270~275 외 블록의 J 순서(J2~J6 개별 조그로 확정 가능), 속도 레지스터.

### Phase 2 — DRFL 연동 (핵심 경로)
> 목표: 축별 토크·전류·온도를 비개입으로 수집.

- [x] CS-01 DRCF 버전 확정 — **V2.11.1**(`GV02110100`). DRFL는 `DRCF_VERSION=2`로 빌드. (DLL↔컨트롤러 호환은 연결 시 `GetSystemVersion`류로 1회 대조 권장.)
- [x] DRFL 헤더 확보·정리 — `DRFL.h`/`DRFLEx.h`/`DRFS.h`/`DRFC.h`를 `refs/`에 보관, 구조체·flat C API·호출규약(`__cdecl`)·`#pragma pack(1)` 확인.
- [x] **네이티브 DLL 확보·번들** — `DRFLWin64.dll` + `PocoFoundation64.dll` + `PocoNet64.dll`(API-DRFL `library/Windows/64bits`)을 `libs/x64/`에 두고 `csproj`에서 **출력 폴더로 자동 복사**(수동 복사 불필요).
  - ✅ 검증: x64 PE 확인, `LoadLibraryEx`로 3개 DLL 로드 성공, 사용하는 export 9개(`_CreateRobotControl`/`_OpenConnection`/`_SetOnMonitoringData`/`_GetSystemVersion` …) 심볼 존재 확인.
- [x] **C# P/Invoke 래퍼** 작성 — `Services/Drfl/DrflStructs.cs`(Pack=1 구조체), `DrflInterop.cs`(`_CreateRobotControl`/`_OpenConnection`/`_SetOnMonitoringData`/`_GetSystemVersion`, `bool`→`I1`).
- [x] `_SetOnMonitoringData` 콜백 등록 → 축별 토크/위치/전류/온도 수신(패시브) — `Services/DrflMonitorService.cs` + `Models/MonitoringSample.cs`. UI에 라이브 축별 표(100Hz→10Hz throttle, 네이티브 스레드→Dispatcher 마샬링). **비개입 보장**: access control/SetRobotMode/SetRobotControl 미호출.
- [x] 빌드 타깃을 DLL 아키텍처에 맞춤 — `csproj` **x64 고정**(`DRFLWin64.dll` 매칭).
- [ ] **실기 검증(하드웨어 필요)**: 실제 컨트롤러 연결 → 콜백 수신·필드 정합(`GetSystemVersion`로 DLL↔v2.11 대조)·축별 값 sanity 확인. ← Phase 2 종료 게이트.
- [ ] (선택) RT 1kHz 경로: `connect_rt_control` → `set_rt_control_output("v1.0", 0.001f, 4)` → `start_rt_control`로 **건강 기준 시그니처** 정밀 측정. ⚠️ 오프라인 전용.

> **실기 1차 검증 결과(192.168.1.100)** — `tools/DrflProbe` 콘솔 프로브로 실측:
> - 네트워크: ping OK, 502/12345 OPEN.
> - **DLL 버전 정합 확인**: API-DRFL `main` DLL = **v1.33.3(`GL013303`, v3 프로토콜)** → `_OpenConnection` 타임아웃 실패. `v2` 브랜치 DLL = **v1.1.18(`GL010118`)** → 연결은 통과하나 컨트롤러가 핸드셰이크 직후 세션 종료(`GetSystemVersion` 실패, 0프레임).
> - 양쪽 다 "Timeout"으로 떨어짐. **모드 배제됨**: Servo On + 자동(Autonomous) 모드에서도 동일 → 로봇 모드/점유 문제 아님. **원인 확정: DRCF 2.11 ↔ DRFL DLL 버전 창.** 컨트롤러는 v2 프로토콜(v3 DLL은 OpenConnection 자체 실패)이나, 보유 v2 DLL(1.1.18)이 2.11엔 너무 구버전. **다음 작업: DRCF 2.11 호환 v2 계열 DRFL 라이브러리 확보**(공개 저장소엔 1.1.18 ↔ 1.33만 존재, 중간 공백). 공개 API-DRFL DLL은 "샘플"이라 미매칭. 공개 Dr.Dart 다운로드센터는 v3.x만 제공. **매칭 v2 라이브러리 입수 경로**: ⓐ Doosan Robot Lab(robotlab.doosanrobotics.com) 로그인 → 이전 버전 다운로드, 또는 ⓑ 이 컨트롤러와 짝인 DART-Studio 설치 폴더의 DRFL DLL 복사. 입수한 `DRFLWin64.dll`+매칭 `Poco*.dll`을 `libs/x64_v2/`에 교체하면 즉시 재검증 가능(나머지는 전부 검증 완료).
> - Modbus(502): UnitId 1/255 · FC03 응답(정지 상태라 값 ~0), FC04 미지원.

### Phase 3 — 데이터 파이프라인 & 저장
- [ ] TimescaleDB 스키마 설계(hypertable): 축별 토크·전류·온도·위치오차·사이클, 타임스탬프.
- [ ] 모니터링 콜백 → 버퍼링/배치 적재.
- [ ] 연결 끊김·재연결 복구 로직.

### Phase 4 — 조건기반 감시(CBM)
- [ ] 정상 사이클 동안 **기준선 학습**(축별 토크·전류 평균/분산).
- [ ] 기준선 대비 편차·추세 기반 임계값 알림.
- [ ] WPF 대시보드: 축별 추세 차트, 건강도 게이지, 알림 리스트.

### Phase 5 — ML 이상탐지
- [ ] 시계열 특징 추출(피처 엔지니어링).
- [ ] Python 학습(Anomalib/sklearn/PyTorch, 비지도) → **ONNX export**.
- [ ] .NET ONNX Runtime으로 앱 내 추론.
- [ ] 이상 점수를 UI/알림에 연동.

### Phase 6 — 비전 진단 (바심 차별화)
- [ ] 카메라 기반 반복정밀도 드리프트·엔드이펙터 마모 진단 PoC.
- [ ] 센서 데이터와 비전 데이터 융합 진단.

### Phase 7 — 제품화
- [ ] 벤더별 드라이버 추상화 인터페이스 설계(`IRobotTelemetrySource` 등) → JAKA, Rokae 추가.
- [ ] 구독/라이선스 모델.
- [ ] 패키징·배포·업데이트.

---

## 7. 작업 컨벤션 (Claude Code 참고)

- 빌드/실행: `DoosanMonitor.sln`(또는 `.csproj`)을 VS2022에서 열고 NModbus 복원 후 F5.
- 아키텍처: MVVM 유지. 연결/통신 로직은 `Services`에, 화면 상태는 `ViewModels`에. 코드비하인드 최소화.
- **로봇 쪽에 프로그램을 다운로드하지 않는다**(무코딩 원칙). 모든 통신은 PC 측에서 컨트롤러로.
- **생산 작업을 방해하지 않는다**(비개입 원칙). 라이브 경로는 패시브 모니터링만.
- 새 기능은 기존 `ModbusConnectionService`/추가될 `DrflService`에 메서드로 붙이고 UI 구조는 유지.

---

## 8. 참고 자료

- **DRFL (오픈소스)**: GitHub `DoosanRobotics/API-DRFL`, `DoosanRobotics/doosan-robot` (`common/include/DRFL.h`)
- **두산 API/매뉴얼**: robotlab.doosanrobotics.com, manual.doosanrobotics.com
- **NModbus**: NuGet `NModbus` (3.x)
- **주요 접속 정보**:
  - 두산 기본 컨트롤러 IP: `192.168.137.100`
  - DRFL 연결 포트: `12345` / RT 제어 포트: `12348`
  - Modbus TCP 포트: `502`
- **Modbus 주소 규칙**: 4xxxx 표기의 `40001` = NModbus의 0-based 주소 `0`.
- **DRCF 버전 호환**: `DRFL.h`의 `DRCF_VERSION` 매크로(v2/v3)로 분기. **본 장비는 v2 → `DRCF_VERSION=2`.**
- **두산 버전 표기 해석**: 티치펜던트 패키지 `GV0X YY ZZ WW` = **V X.YY.ZZ** (예: `GV02080000`=V2.8.0, `GV02110100`=V2.11.1). 프리픽스 `GV`=v2 계열, `GF`/`03xx`=v3 계열.
