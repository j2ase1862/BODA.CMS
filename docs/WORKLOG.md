# 작업 로그 (Work Log)

> 세션별 작업 기록. 프로젝트 기준 문서는 [`ROADMAP.md`](../ROADMAP.md)이며, 이 파일은 "무엇을, 왜, 어떻게 했고 무엇을 발견했는지"의 시간순 기록입니다. 최신 항목이 위로 옵니다.

---

## 2026-07-22 (22) — v0.7.0 배포 패키징 (UR 포함 첫 패키지)

### 배경
- UR 실기 검증 시도 중 제조사 콤보에 UR 미표시 — 원인은 코드가 아니라 **구버전 빌드 실행**(Debug 7/15 13:35·Release 7/13, 둘 다 UR 커밋 `9bb4f29` 13:47 이전 산출물). 기존 배포 패키지(v0.6.2, 7/13)도 동일하게 UR 이전이라 재패키징.

### 작업 내용
- 솔루션 재빌드(경고 0·오류 0) 후 `tools\package.ps1 -Version 0.7.0` — setup 번들(396.4MB)·앱 MSI(68.4MB)·Collector MSI(43.5MB)·zip 2종 생성.
- 앱 zip에 `BODA.CMS.Drivers.UR.dll` 포함 확인 — v0.7.0이 UR 벤더가 들어간 첫 배포 패키지.

### 검증
- `dotnet test` 72/72 통과. WiX 경고 1건(Collector.wxs ServiceConfig — 기존과 동일, 동작 무관).
- UR 실기 검증(ROADMAP §5.5)은 새 빌드로 진행 예정 — 결과에 따라 §5.5 체크박스 갱신.

---

## 2026-07-15 (21) — UR(Universal Robots) 온보딩: RTDE 드라이버 + 채널 등급 수동 선택

### 배경
- 두산 실기 데이터 수집 확인 후 4호 벤더로 UR 착수(§5.4 후보 풀 1순위 — 점유율 1위, RTDE가 문서 공개·수신 전용이라 비개입 정합성 최상). UR은 관절 토크센서가 없어 자동 등급 판정(전류 기준 Pro)과 영업 등급 사이 괴리가 생길 수 있어, 채널 등급을 수동 선택하는 UI 를 함께 추가.

### 작업 내용
- **`Drivers.UR` 신설** (외부 SDK·네이티브 DLL 없음 — 순수 C#): `RtdeProtocol`(v2 패키지 조립·해석 — 버전 협상 V → 출력 구독 O → 시작 S → 데이터 U, 빅엔디언) + `RtdeFramer`(TCP 분할·병합 경계 복원) + `UrRtdeSource`(TCP 30004, 125Hz 요청, 무수신 5s→Faulted, 재연결 시맨틱).
- 신호 정규화: `actual_q`/`actual_qd` 라디안→도, `actual_current`(A)·`joint_temperatures`(℃) 그대로, `target_moment`→모델토크(Nm). 실측 토크는 null(토크센서 미탑재 — 0 채움 금지 규약). 레시피 타입 검증: `NOT_FOUND`/타입 불일치 시 변수명을 담아 연결 거부.
- 카탈로그 등록: WPF 앱·Collector 각 1항목("ur") — 코어 파이프라인 수정 없음(§3 격리 원칙 통과 확인).
- **채널 등급 수동 선택(콤보)**: 카드 헤더 '등급' 콤보(자동/Basic/Pro, 모니터링 중 비활성). `ProductTierEvaluator.Effective` 신설 — 자동 판정을 넘는 상향 불가. 하향 선택 시 `TierSignalFilter`(Core 신설)가 심층 신호(전류·토크·온도)를 프레임 단계에서 차단 — CBM·ML·차트·표가 전부 걸러진 프레임만 보므로 Basic 선택이 라이선스 게이팅 우회가 되지 않는다. 라이선스 검사도 유효 등급 기준.

### 검증
- 유닛테스트 11건 추가: UR 5건(프레이머 분할 복원, 프로토콜 왕복, Pro 판정, 가짜 컨트롤러 핸드셰이크→rad→° 정규화→원격종료 Faulted, NOT_FOUND 거부) + 등급 정책 6건(자동/하향/상향 불가/필터의 제거·보존·무복사) — **72/72 통과, 빌드 경고 0**.
- URSim 통합(§6 7단계)은 이 PC에 도커가 없어 미실시 — ROADMAP §5.5 에 🔶 게이트로 기록 (도커 가능 PC에서 `universalrobots/ursim_e-series`로 실기 없이 검증 가능).

---

## 2026-07-13 (20) — CBM 기준선 학습창 설정화 (로봇 작업 사이클 기반)

### 배경
- 현장 증상: 기준선 재학습 후에도 건강도가 70~80 사이를 진동. 원인은 기준선 학습창(60초 고정)이 로봇 작업 1사이클보다 짧아, 기준선에 안 담긴 사이클 구간이 돌 때마다 z≈2 로 출렁이는 구조적 문제 — 하드코딩을 현장 설정으로 전환.

### 작업 내용
- **`Collector:Cbm` 설정 신설**(`CbmSettings`): `CycleSeconds`(작업 사이클, 초) × `CyclesToLearn`(기본 3) 으로 학습창 자동 산정, `LearningSeconds` 직접 지정이 우선, 하한 30초. 미설정 시 기존 60초 그대로 — 기존 현장 무영향. 기동 로그에 유효 학습창과 산정 근거 출력.
- **ML 파이프라인 정합 유지**: `retrain_anomaly.py --learning-aggregates`(기본 60) 신설 + sidecar 에 기록, `retrain.ps1 -LearningSeconds`, 앱 재학습 창에 '학습창(초)' 입력(기본 60, 30초 미만 거부) — Collector 설정을 바꾸면 재학습 시 같은 값을 넣어야 한다는 도움말 포함.
- WPF 앱 카드 자체의 CBM 은 기본 60초 유지(진단용) — 무인 감시 기준은 Collector/대시보드.

### 검증
- 유닛테스트 7건 추가(기본값·사이클 계산·올림·직접 지정 우선·30초 하한·횟수 0 방어·CbmOptions 변환) — 61/61 통과.
- E2E: `Collector__Cbm__LearningSeconds=120` 로 기동 → 32초 시점 learningProgress 0.27(≈32/120) 확인 — 바인딩·배선 정상. `--learning-aggregates` CLI 노출 확인.
- 현장 가이드: `C:\BODA\Collector\appsettings.json` 의 `Collector:Cbm:CycleSeconds` 에 작업 사이클(초) 기입 → 서비스 재시작(또는 대시보드 '수집기 재시작') → 정상 운전 중 '기준선 재학습'. 이후 앱 AI 재학습 시 학습창(초)에 같은 유효값 입력.

---

## 2026-07-13 (19) — 대시보드 운영 버튼 (기준선 재학습 · 알림 리셋 · 수집기 재시작)

### 배경
- 현장에서 모델 재학습 후 "건강도만 계속 하락, ML 은 정상" 증상 — CBM 기준선이 서비스 재시작 직후(정지·웜업 상태) 첫 60초로 고정 학습된 것이 원인. 지금까지는 관리자 PowerShell `Restart-Service` 가 유일한 복구 수단이었다.

### 작업 내용
- **`CbmMonitor.Reset/ClearActiveAlerts`, `MlAnomalyMonitor.Reset/ClearActiveAlerts`**: 무중단 기준선 재학습(다음 프레임부터 재학습, z 축척이 바뀌므로 ML 윈도 동반 리셋)과 활성 알림 해제(기준선 유지 — 조건 지속 시 디바운스 후 재알림).
- **REST 3종(내부망)**: `POST /api/cbm/relearn`(전 채널 리셋), `POST /api/alerts/clear`(활성 알림 해제 + 대시보드 목록 리셋 — `AlertQuery.AfterUtc` 로 DB 이력은 보존·숨김만, 재기동하면 다시 보임), `POST /api/service/restart`(0.5초 후 `Environment.Exit(1)` — 서비스 복구 정책 restart/10s 가 재기동, 콘솔 실행은 그냥 종료).
- **대시보드 툴바**: 라이선스 줄 아래 칩 버튼 3개 + 결과 메시지. 재학습·재시작은 confirm 안내(정상 운전 중 실행 조건, 재시작 중단 시간) 포함.

### 검증
- 콘솔 수집기 + 시뮬레이터 E2E: 학습 완료(health 92/56) → relearn 즉시 Learning 8% 복귀 → 알림 5건이 clear 후 0건(DB 경로 `time > cleared` 필터 동작) → restart 로 프로세스 종료 확인. 헤드리스 Edge 로 툴바 렌더 확인. 유닛테스트 54/54, 빌드 경고 0.

---

## 2026-07-13 (18) — 앱 내 AI 재학습 (DB 구간 선택 → 학습 → 모델 교체·핫리로드)

### 배경
- 현장에서 관리자 PowerShell 로 `tools\retrain.ps1` 을 돌리는 방식은 진입장벽이 높고, 현장 PC 의 스크립트 사본이 레포와 어긋나는 사고(배포 단계 `Join-Path` null)도 발생 — 앱 버튼 하나로 대체.

### 작업 내용
- **재학습 창(헤더 'AI 재학습' 버튼)**: `Views/RetrainWindow` + `ViewModels/RetrainViewModel`.
  ① 데이터 조회 — telemetry_frames 를 로봇×채널로 집계(min/max/count, 앱에 Npgsql 8.0.5 추가)해 콤보로 제시, 선택 시 학습 구간·필터 자동 채움(수동 편집 가능). ② 재학습 — 진행 로그 실시간 표시, 취소 지원. ③ 교체 — 앱·Collector 서비스 models\ 동시 교체(기존은 `backup-일시\` 보존) 후 서비스 재시작.
- **파이프라인(`Services/ModelRetrainService`)**: 학습은 검증된 Python 파이프라인(`tools\ml\retrain_anomaly.py`, exe 옆에 번들) 서브프로세스 재사용 — `PYTHONUTF8` 강제로 CP949 로그 깨짐 차단, Npgsql→libpq DSN 변환, 취소 시 프로세스 트리 kill. 배포는 쓰기 가능+서비스 없음이면 인프로세스 복사, 아니면 승격 PowerShell(UAC 1회, BOM UTF-8 생성 스크립트)로 서비스 중지→교체→재시작 — 승격 프로세스는 stdout 을 못 받으므로 로그 파일 경유로 UI 에 회수. Collector 경로는 서비스 레지스트리 `ImagePath` 에서 역추적.
- **모델 핫리로드(앱 재시작 불필요)**: `MlAnomalyMonitor.Detach` 신설 + `OnnxAnomalyScorer` Dispose 를 게이트 안으로(교체 중 마지막 스코어링과의 경합 시 '정상' 점수로 안전 탈출). `TelemetrySourceViewModel.ReloadMlModel` — 기존 모니터를 CBM 스트림에서 떼고 백그라운드 재로드, `MainViewModel.ReloadMlModels` 가 전 카드에 전파.

### 검증
- UIA E2E(실 DB, 7-08 수집분 22,838 프레임): 조회 → 구간 자동 채움 → 학습(시리즈 30, 실 윈도 32,250 + 합성 25%) → ONNX↔sklearn 오차 0.000000 → bin models\ 교체(backup 생성·sidecar trainedAtUtc 갱신) → 카드 핫리로드까지 상태 '완료' 확인. DB 미접속·UAC 거부·Python 부재는 상태문구+수동 적용 안내로 처리. 유닛테스트 54/54, 빌드 경고 0.
- 참고: 현장 PC 적용은 새 빌드 배포 필요. 기존 `tools\retrain.ps1` 도 그대로 동작(병행 유지).
- **v0.6.0 패키징**: `tools\package.ps1 -Version 0.6.0` — setup 번들(396MB)·앱/Collector MSI·zip 생성, 앱 패키지에 `tools\ml\retrain_anomaly.py`·Npgsql·models 포함 확인.

---

## 2026-07-10 (17) — 로봇 상태 3D 시각화 (카드 3모드: 표·차트·로봇)

### 작업 내용
- **카드 3모드**: `CardView` enum(Table/Chart/Robot) + 세그먼트 라디오 버튼(EnumToBoolConverter). 헤더에 CBM 건강도 원형 게이지(HealthGauge) 상시 표시.
- **로봇 모드(RobotStatusView)**: HelixToolkit.Core.Wpf(MIT) 3D 협동로봇 — 자세 = 실시간 관절 각도(`JointPositionDeg`), 관절 마커 색 = CBM 기준선 이탈(z<2 초록/<4 주황/≥4 빨강, 학습 전 회색). 드래그 회전·휠 확대. + 축×신호 z 히트맵(툴팁에 z값) + 축별 미니 추세선(토크→전류→온도 우선, 5Hz×30초 링버퍼).
- **현실성**: 실물처럼 상완·전완을 서로 다른 측면 평면(z 오프셋)에 배치해 접힐 때 자기관통 없음 + 렌더링 관절 가동범위 클램프(J2 ±115°/J3 ±150°/J5 ±120° — 실로봇 각도는 한계 안이라 무왜곡, 시뮬레이터 무제한 사인파만 제한).
- **데이터 노출**: `CbmMonitor.DetailSnapshot`(신호·축별 LastZ/DriftZ/AlertActive/Learned — `CbmAxisDetail`), VM 에 `View`/`HealthScore`/`LastFrame`/`CbmDetails`.
- 갱신은 5Hz UI 타이머, 로봇 모드가 아니면 시각 갱신 없음(추세 이력만 축적) — 저사양 배려 유지.

### 검증
- 시뮬레이터 E2E(UIA): 학습 완료 후 관절 전체 초록 → Pro 채널 t=100s J3 결함 주입 후 J3 빨강·J5 주황이 3D 관절/히트맵/게이지(건강도 0)에 동시 반영 스크린샷 확인. 유닛테스트 54/54, 빌드 경고 0.
- 미확인: 실로봇(두산) 자세 매핑 — 기본자세 오프셋(RestOffsets)·부호 규약이 실물과 다를 수 있어 현장 확인 후 벤더별 보정 필요.

---

## 2026-07-10 (16) — 앱 UI 다크 테마 리디자인

### 작업 내용
- **디자인 시스템 신설**: `Themes/Theme.xaml` — 웹 대시보드(wwwroot)·앱 아이콘과 동일 팔레트(배경 #14171B, 카드 #1B1F24, 텍스트 #D6E1EC, 액센트 청록 #33C9BA). Button(액센트)·ToggleButton(고스트→체크 시 액센트)·TextBox·ComboBox(전체 다크 템플릿)·CheckBox(신호 선택을 필 칩으로)·슬림 스크롤바 implicit 스타일 + Card/InsetPanel 공용 스타일.
- **MainWindow**: 브랜드 헤더(워드마크+부제) 추가, 카드 DropShadowEffect 제거(플랫 + 헤어라인 — 저사양 렌더 부담도 감소), 인라인 색상 전부 테마 리소스로.
- **상태 색 일원화**: `Mvvm/Theme.cs`(Ok/Warn/Bad/Muted, Freeze) — ViewModel 의 Brushes.SeaGreen/DarkOrange/Firebrick/Gray/OrangeRed 를 팔레트로 치환 (다크 배경 가독 색).
- **다크 타이틀바**: `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` — Win10 1809+/Win11, 미지원 OS 무시.
- HelpIcon 다크 톤 + 액센트 호버.

### 검증
- 시뮬레이터 2채널 실행 상태로 표/차트 모드 스크린샷 확인(UIA 자동화) — 칩·콤보 팝업·차트·타이틀바 모두 다크 일관. 유닛테스트 54/54, 빌드 경고 0.

---

## 2026-07-10 (15) — 저사양 PC(i3-6100/8GB) UI 멈춤 수정

### 작업 내용
- **증상(현장)**: 앱에서 모니터링 중지/연결 버튼이 먹통·데드락처럼 보임.
- **원인 1 — DRFL 해제가 UI 스레드에서 동기 실행**: `DoosanDrflSource.DisconnectAsync` 가 네이티브 `CloseConnection`+`DestroyRobotControl` 을 호출 스레드에서 실행 — 네이티브가 내부 수신 스레드 정리를 기다리며 수 초 블로킹 가능(콜백에서 CBM/ML 추론이 도는 저사양에서 악화). Connect 는 이미 Task.Run 이었는데 Disconnect 만 동기였음. → `Task.Run` 오프로드 + `SemaphoreSlim` 으로 연결/해제 직렬화(버튼 연타·제조사 전환 중복 방지). ConnectAsync 초입의 정리용 Disconnect 도 동일 오프로드.
- **원인 2 — ONNX 기본 세션의 CPU 점유**: 기본값은 코어 수만큼 intra-op 스레드 + 스핀 대기 — 2C4T 에서 유휴에도 CPU 점유. 이 모델(소형 iforest, 1×6 입력)은 단일 스레드로 충분 → `IntraOpNumThreads=1, InterOpNumThreads=1` (앱·Collector 공용 경로라 둘 다 혜택).
- **원인 3 — UI 갱신 부하**: 차트 전체 리렌더(6축×수천 점, Skia CPU) 10Hz + 판독 표 10Hz → 각각 5Hz 로 (체감 차이 없음, UI 스레드 여유 확보).

### 검증
- 빌드 경고 0, 유닛테스트 54/54. DRFL 실기 블로킹 재현은 로봇 없이 불가 — 수정 패턴은 기존 ConnectAsync 의 Task.Run 경로와 동일 시맨틱. 현장 0.5.2 적용 후 중지/연결 반응성 확인 필요.
- **0.5.2 배포본 시뮬레이터 E2E (UIA 자동화)**: 제조사 전환 → 2채널 시작 → 수신 중 2/2 → 차트 2개 → 중지. 중지 버튼 클릭→완료 57~61ms, 중지 직후 UI 스레드 응답 10ms(SMTO_ABORTIFHUNG), 재시작 정상. 정상 상태 CPU ≈ 0.1~0.2 코어어치(표/차트 모드 모두 — i3-6100 4LP 기준 3~5%). 기동·제조사 전환 직후 ML ONNX 콜드 로드 동안 ~1.4 코어어치 일시 점유(백그라운드, UI 무관 — 수십 초 내 소멸).

---

## 2026-07-10 (14) — 대시보드 알림: DB 이력 조회 + 카드 클릭·심각도 필터

### 작업 내용
- **문제**: 웹 알림이 메모리 링 200건·화면 40건 상한에서 멈춰 보임 — DB(`telemetry_alerts`)에는 전체가 있는데 웹이 링 꼬리만 노출.
- **저장소**: `IFrameStore.QueryAlertsAsync(AlertQuery)` 추가 — Timescale 구현은 robot/channel/severity(쉼표 목록·대소문자 무시)/before(페이지 커서) 동적 WHERE + `ORDER BY time DESC LIMIT`. dry-run(LogFrameStore)은 null 반환.
- **API**: `/api/alerts?robot=&channel=&severity=&before=&take=` — DB 조회 우선, 실패(미기동)·dry-run 이면 `DashboardState.GetAlerts(AlertQuery)` 메모리 링 폴백(동일 필터).
- **UI(혼용 방식)**: 카드 클릭 → 해당 로봇·채널 알림만(재클릭 해제, 선택 카드 테두리 강조) + 심각도 칩(전체/경고 이상/알람만) — 두 입력이 같은 필터 상태 공유. "이전 알림 더 보기" = before 커서 페이지 추가 로드(보는 동안 자동 갱신 일시정지, "▶ 최신으로"로 복귀).

### 검증
- 콘솔 Collector 실기동: 무필터 162건(=DB count) / alarm 30 / warning,alarm 123 / robot+channel 141 — 모두 psql 집계와 일치. before 커서 2페이지 중복 0·시각 침범 0. 헤드리스 Edge 스크린샷으로 카드·칩·알림 렌더 확인. 빌드 경고 0.
- 미검증: 카드 클릭·더 보기 등 마우스 상호작용 실연(수동 확인 권장), DB 폴백 경로 실기동(코드 경로 단순).

---

## 2026-07-09 (13) — 앱 제조사 선택 → 웹 대시보드 자동 반영

### 작업 내용
- **문제**: 앱에서 제조사를 바꿔도 대시보드는 Collector 의 appsettings.json 대로(두산) 수집 — json 수동 수정 + 서비스 재시작 필요했음.
- **Collector — 실행 중 로봇 재구성**: `CollectorService.ReconfigureAsync` — 펌프 세트에 링크드 CTS 도입, "현재 세트 취소 → 종료 대기 → `DashboardState.ClearChannels()` → 새 세트 기동"을 세마포어로 직렬화. Robots 비어도 서비스가 살아서 재구성 대기(기존엔 return).
- **REST**: `GET /api/robots`(현재 구성) + `PUT /api/robots`(전체 교체 — 벤더 카탈로그 검증, RobotId 중복 거부, 적용 후 appsettings.json 의 Robots 만 JsonNode 로 치환 영속화·나머지 설정 보존). 내부망 전용 API(대시보드와 동일 신뢰 모델).
- **앱 — `Services/CollectorSync`**: 제조사 전환·연결 성공 시 PUT 호출(3초 타임아웃, 최선 노력·결과는 앱 로그). 대상 기본 localhost:5100, 원격은 `BODA_COLLECTOR_URL`. **감시 로봇이 2대 이상이면 자동 반영 생략**(1대 진단 화면이 현장 다중 구성을 덮지 않게). 같은 벤더면 기존 RobotId(현장 이름) 보존.
- **대시보드 즉시 노출**: 펌프의 `Register` 를 ML 로드(콜드 10초+) 앞으로 — 전환 후 카드가 바로 뜨고 ML 은 준비되면 갱신 재등록.

### 검증
- 콘솔 Collector 실기동 E2E: sim 기동 → PUT jaka 교체 {robots:1,pumps:1} → 2초 내 /api/status 에 jaka-01/monitor 노출 → bin appsettings.json 에 Robots 만 교체·Storage/Urls 보존 확인 → 미등록 벤더(fanuc) 400 거부. 유닛테스트 54/54.
- 미검증: WPF 앱 UI 조작 → 대시보드 반영 실연(수동 확인 권장 — 앱 로그에 "감시 서버(대시보드)에 반영됨" 문구).

---

## 2026-07-09 (12) — 통합 설치 번들(PostgreSQL 동봉) + DB 자가 구성

### 작업 내용
- **`installer/Bundle.wxs` (WiX Burn)**: `BODA.CMS-collector-setup-{v}-x64.exe` (396MB) — 체인: PostgreSQL 16 무인 설치(레지스트리 `HKLM\SOFTWARE\PostgreSQL\Installations` 감지로 기설치 시 건너뜀, `Permanent`) → Collector MSI. EDB 설치본을 번들에 **동봉**해 오프라인 현장서 단일 파일로 끝. DB 암호 기본 `postgres`(appsettings 기본 접속 문자열과 일치·localhost 전용이라 무작위 암호의 보안 이득 없음, `setup.exe PgPassword=...`로 재정의 가능). EDB 설치본은 `dist/cache`에 캐시(패키징이 최초 1회 다운로드).
- **DB 자가 구성(코드)**: `TimescaleFrameStore.EnsureDatabaseAsync` — 접속 시 3D000(DB 없음)이면 유지보수 DB(postgres)로 붙어 `CREATE DATABASE` 후 진행. psql/스크립트 개입 불필요.
- **초기화 견고화**: `StorageWorker`가 `InitializeAsync` 실패를 백오프 재시도로 감쌈 — 기존엔 예외가 그대로 새어 .NET 8 기본(StopHost)으로 **호스트 전체가 내려갔음**. MSI가 서비스를 설치 직후 자동 시작하므로 "DB가 늦게/안 뜨는" 상황이 정상 경로가 됨 → 필수 수정.
- install-db.ps1은 특수 케이스용으로 유지(원격 DB·기존 PG 재사용·MSI 단품에 나중 추가). 사용설명서 §10: 설치 1단계를 setup.exe로, DB 준비 절은 "대부분 필요 없음"으로 재구성.

### 검증
- DB 자가 구성 실검증: 로컬 PG17 대상 없는 DB명으로 Collector 기동 → "데이터베이스 …가 없어 새로 만들었습니다" + telemetry_frames/alerts 생성 확인(후 정리). 죽은 포트 대상 기동 → 초기화 실패 백오프 재시도 로그·프로세스 생존 확인. 유닛테스트 54/54.
- 번들 추출(`wix burn extract`)로 동봉 페이로드 2건 확인 — a0=EDB exe(SHA256 원본 일치), a1=Collector MSI(D0CF 시그니처).
- 미검증: 클린 PC에서 setup.exe 전 과정(PG 무인 설치 포함) 실행.

---

## 2026-07-09 (11) — MSI 설치 방식 전환 + PostgreSQL 자동 설치 스크립트

### 작업 내용
- **MSI 전환(WiX 5)**: `installer/App.wxs`(모니터 앱 — Program Files\BODA.CMS, 시작 메뉴·바탕화면 바로가기) + `installer/Collector.wxs`(감시 서버 — C:\BODA\Collector, **서비스 자동 등록·시작·delayed-auto·10/30/60초 장애 재시작까지 MSI가 처리**, install-service.ps1 불필요). `tools/package.ps1`이 WiX 자동 설치 후 MSI 2종 + 보조 zip 2종을 빌드.
  - `appsettings.json`은 `NeverOverwrite+Permanent` — 업그레이드·재설치·제거에도 현장 로봇 구성 보존. 업그레이드는 새 msi 더블클릭이면 끝(MajorUpgrade).
  - 비표준 루트(C:\BODA)라 `ComponentGuidGenerationSeed` 필요(WIX0231) — 시드 GUID는 업그레이드 동일성의 근거이므로 변경 금지.
  - **WiX 7은 OSMF(상용 유지보수비) 동의 요구** → 5.0.2로 고정(package.ps1 부트스트랩도 5.0.2 명시).
- **`tools/install-db.ps1`**: 현장 PC PostgreSQL 원커맨드 구성 — EDB 설치본 무인 설치(다운로드 or `-InstallerPath` 오프라인) → `boda_cms` 생성 → TimescaleDB 확장 있으면 활성화 → appsettings.json에 접속 문자열 기록(+Storage on, 정규식 치환이라 로봇 구성·한글 보존) → Collector 서비스 재시작. 기존 PG 재사용은 `-Password` 필수.
- 사용설명서 §10 재작성: 설치 요구사항 표(DB는 선택), DB 준비(자동/수동), 설치 순서 4단계(msi 더블클릭 중심), 업데이트·제거를 프로그램 추가/제거로.

### 검증
- MSI 관리 이미지 추출(`msiexec /a`)로 361파일 구성 확인(exe·appsettings·models·tools·wwwroot), MSI DB 테이블 조회로 ServiceInstall(auto)·ServiceControl(163)·MsiServiceConfig(delayed)·FailureActions·바로가기 2종 확인. install-db.ps1은 EDB URL 실재(200, 357MB)·appsettings 치환 로직 검증.
- 미검증(환경 제약): 실제 msi 설치/업그레이드/제거 사이클 — 개발 PC에 기존 스크립트 설치본·PG17이 있어 클린 PC 실테스트 필요.

### 주의
- 기존 zip+스크립트로 설치된 현장은 MSI 설치 전 `install-service.ps1 -Uninstall` 선행(서비스 이름 충돌).

---

## 2026-07-08 (10) — 실기 데이터 축적 개시 (TimescaleDB) + ML 재학습 스크립트

### 작업 내용
- **실기 전환 후 대시보드가 시뮬레이터 데이터를 보이던 원인**: Collector `appsettings.json`의 `Robots`가 `sim`으로 남아 있었음 — 웹 대시보드는 WPF 앱이 아니라 Collector가 자기 설정대로 수집한 것을 보여준다. 소스 appsettings도 실행본과 동일하게 `doosan-01`(192.168.1.100)로 정렬(재빌드 시 sim으로 회귀 방지) + `Storage.Enabled: true`.
- **저장 인프라 구축(현장 PC)**: PostgreSQL 17 winget 무인 설치 → `boda_cms` DB → TimescaleDB 2.28.1(PG17용 공식 zip, 수동 배치: lib/extension 복사 + `ALTER SYSTEM SET shared_preload_libraries`) → `CREATE EXTENSION` → 기존 `telemetry_frames`를 `migrate_data => TRUE`로 hypertable 전환(무중단 — StorageWorker가 PG 재시작 동안 배치 보존·재시도로 자가 복구함을 실확인). drfl+modbus 2채널 적재 검증.
- **`tools/ml/retrain_anomaly.py`**: 실 데이터 재학습 스크립트(부트스트랩 대체). 런타임 z-파이프라인 재현 — 1초 버킷 평균(SQL `unnest WITH ORDINALITY`, modbus는 `vendor_raw` jsonb 배열 키까지) → 수집 공백 기준 세그먼트 분리 → 세그먼트별 첫 60집계 기준선 + σ 하한(1e-3, 0.01·|μ|) → z → 10집계 윈도 6피처. 합성 정상 blend 옵션(기본 25%) — 실 데이터가 못 본 정상 형태 과잉 탐지 완화. ONNX+사이드카 export는 부트스트랩과 동일 규약.

### 발견/검증
- **기준선 캡처 타이밍이 판정 품질을 지배**: STANDBY 중 재시작 → 기준선이 정지 신호로 학습 → 가동 시 z 800~1500 폭주(오탐). 가동 중 재시작으로 해소 확인. 사용 규약: **로봇이 정상 사이클을 도는 중에 모니터링을 시작할 것**.
- **부트스트랩 모델의 평탄 신호 사각지대 실증**: J6 토크/외란 ML 플래핑(score -0.17~-0.24 vs 임계 -0.157, 수십 초 주기 발생↔복귀) — 실측은 J6 토크 -0.16±0.03 Nm 완전 평탄, CBM 건강도 100. 거의 상수 신호의 양자화 계단 z-궤적을 합성 AR(1) 모델이 낯설어하는 것. 실 데이터 재학습으로 해소 예정.
- 재학습 스크립트 스모크 테스트(실 DB ~1h, `--out` 임시 폴더): 시리즈 42개(drfl 5신호×6축 + modbus 2신호×6축), 윈도 30,132개, ONNX↔sklearn 점수 오차 0.000000.

### 남은 것
- 정상 운전 데이터 1일+ 축적 후 `retrain_anomaly.py --since <가동시각>` 실행 → bin의 `models/` 교체 → 재시작.
- ~~보존 정책 미설정~~ → `add_retention_policy('telemetry_frames', '30 days')` 적용(job 1000, 일 1회 실행 — 30일 지난 청크 자동 삭제). `telemetry_alerts`는 일반 테이블(저빈도)이라 정책 밖.

---

## 2026-07-08 (9) — 사용설명서에 운영 배포 섹션 추가

- `docs/사용설명서.html`에 **§10 관리자용 — 감시 서버 설치·운영**(설비/IT 담당자 대상) 추가:
  - 프로그램 역할 구분표(모니터 화면 vs 감시 서버), 설치 5단계(zip 해제 → Robots 등록 → 라이선스 배치 → 서비스 등록 → 확인 — 실검증된 절차 그대로)
  - 다른 PC에서 대시보드 열기(Urls 0.0.0.0 + 방화벽, 내부망 한정 경고), 일상 운영 표(설정 반영·이벤트 뷰어 로그·업데이트·제거·DB 켜기)
- 쉬운 용어 유지: Collector→"감시 서버", 서비스 등록 효과를 "재부팅해도 자동 시작, 비정상 종료 시 스스로 재시작"으로 풀어씀.
- headless Edge 렌더 확인.

---

## 2026-07-08 (8) — Collector Windows 서비스 지원 + 기동 블록 결함 수정

### 작업 내용
- **Windows 서비스 지원**: `Microsoft.Extensions.Hosting.WindowsServices` — 실제 서비스로 실행될 때만 배선(`WindowsServiceHelpers.IsWindowsService()` 가드 — 무조건 호출하면 콘솔 모드에 EventLog 로거가 끼어듦). 서비스 실행 시 CWD가 System32라 appsettings를 못 찾는 문제 → `ContentRootPath = AppContext.BaseDirectory` 고정.
- **`tools/install-service.ps1`**: 설치/제거(-Uninstall)/시작 — 관리자 체크, `delayed-auto` 시작(부팅 직후 네트워크 준비 전 기동 실패 회피), 비정상 종료 시 자동 재시작(10s/30s/60s), 이벤트 뷰어 로그 안내. 운영은 `-ExePath`로 배포 폴더 exe 지정 권장.
- **기동 블록 결함 발견·수정** (서비스 검증 중 발견): ONNX InferenceSession 콜드 로드가 **10~15초** 걸리는데(웜 캐시면 1~2초 — P5 데모에서 못 본 이유) 이것이 기동 경로를 막고 있었다:
  - Collector: 펌프의 ML 로드가 `StartAsync`를 블록 → Kestrel 바인딩 15초 지연(서비스 SCM 타임아웃 위험) → `RunPumpAsync` 진입 즉시 `Task.Yield()`로 스레드풀 양보. **수정 후 바인딩 2초 내.**
  - WPF: 카드 VM 생성자에서 UI 스레드 ML 로드 → 앱 첫 기동 프리즈(과거 "창이 늦게 뜨던" 원인) → 백그라운드 로드 후 배선("ML 로드 중…" 표시), volatile 공개.
- **잡초 제거**: 자동 생성돼 커밋에 섞였던 `Collector/Properties/launchSettings.json` 삭제 — `dotnet run`의 포트를 무작위(56624/56625)로 가로채 appsettings `Urls`(5100)를 무시하게 만들던 원인.

### 검증
- 빌드 경고 0, 테스트 54/54. exe 직접 기동: 2초 내 5100 바인딩, ONNX 로드 완료 후 basic/pro 채널 + ML 부착 확인.
- **실 서비스 설치 테스트 완료**(UAC 승격): 설치→Running(세션 0, StartType Automatic/delayed)→API 200·수집/CBM/ML 동작→이벤트 뷰어에 "Service started successfully"+앱 로그 기록→제거까지 왕복 확인.
- 서비스 테스트가 결함 하나 더 발견: **wwwroot가 빌드 출력에 미복사**(Web SDK는 publish 때만) → bin에서 exe/서비스 실행 시 대시보드 404. csproj `Content Update` 복사로 수정 — 빌드 출력 실행도 index 200 확인. (배포 zip은 publish 경로라 원래 정상이었음.)

---

## 2026-07-08 (7) — 현장용 간단 사용설명서 (HTML)

- `docs/사용설명서.html` — 자체 완결형(외부 의존성 없음, 인쇄 친화) 현장 사용자 매뉴얼.
- 쉬운 용어 원칙: 본문은 쉬운 말, 화면 표기는 괄호 대응 — CBM→"건강 상태 감시", ML→"AI 이상 감지", 기준선→"평소 상태", z→"벗어난 정도", 드리프트→"서서히 변함", 패시브→"읽기 전용".
- 구성: 3분 시작 가이드 → 화면 읽는 법(상태등·등급·체크박스) → 건강 점수/AI 감지 해설 → **알림별 권장 조치**(급변=즉시 확인, 드리프트=추세 관찰, 복귀=기록) → 차트 → 웹 대시보드 원격 조회 → 문제 해결 표 → 용어 사전(화면 표기 ↔ 쉬운 말 13항목).
- 학습 중 주의(정상 작업 중 학습해야 함 — 고장 상태를 "평소"로 외우는 함정) 명시.
- headless Edge 렌더 확인.

---

## 2026-07-08 (6) — Todo List.md + 도움말 아이콘(HelpIcon)

### 작업 내용
- **`Todo List.md`(루트)**: 남은 작업 전체를 벤더별/영역별로 정리 — Rokae(SDK zip→클라이언트→등록→실기), JAKA(실기 매핑·Modbus 맵), 두산(DRFL DLL·스케일 확정), 파이프라인(실 DB)·CBM/ML(영속화·재학습)·비전(실 카메라)·제품화(운영 키·통보·SaaS). 🔶 = 외부 확보물 필요 표시. ROADMAP이 소스오브트루스 — 완료 시 양쪽 갱신 규칙 명시.
- **`Views/HelpIcon`**: 클릭 토글식 도움말 아이콘(?) — `HelpTitle`/`HelpText` DP, 다크 팝업(제목+본문, MaxWidth 340), 바깥 클릭 시 자동 닫힘(IsOpen↔IsChecked 양방향), UIA 접근성 이름("도움말: {제목}").
- 배치 11곳: 연결 카드(제조사·IP·포트·연결 상태) + 채널 카드×2(카드 헤더 통합 설명·차트 토글·채널 포트, 차트 모드에서 차트 신호) + CBM 알림 카드. 설명은 등급 자동 판정·패시브 의미·CBM/ML 칩·알림 종류(급변/드리프트/ML/복귀)와 색상 규칙까지 커버.

### 검증
- 빌드 경고 0, 테스트 54/54. 실행 후 UIA로 아이콘 11개 열거 확인, '채널 카드' 팝업 오픈 캡처(스크린샷 — 팝업은 별도 HWND라 PrintWindow 아닌 화면 캡처 필요).

---

## 2026-07-08 (5) — Rokae 온보딩 (3호 벤더 — 조사 + 드라이버 본체)

### 요약
- §6 1·2단계를 **실제 웹 리서치로 수행**(추측 프로토콜 구현 금지 원칙) — 공식 SDK 존재 확인(RokaeRobot GitHub, Apache-2.0, C# 포함).
- 와이어 프로토콜이 비공개(프리빌트 SDK)라 통신부를 `IRokaeStateClient`로 추상화하고 **드라이버 본체(폴링·정규화·재연결·Pro 판정)를 완성** — SDK zip 확보 시 클라이언트 구현체만 추가(ML의 IAnomalyScorer 패턴 재적용).

### 조사 확정 사항
- xCoreSDK-CSharp: C++/CLI 래퍼(xCoreSDK_cli.dll), x64 Windows, .NET≥5, NuGet 없음(Releases zip). **비실시간 인터페이스 전용** → 상태 조회 폴링이 곧 패시브 채널.
- RCI 1kHz = 제어 장악 → 오프라인 전용 분류(두산 RT 동일). Modbus는 확장 IO 용도만 확인 — 텔레메트리 채널 보류.

### 구현
- `Drivers.Rokae`: `RokaeXCoreSource` — 10Hz 폴링, 클라이언트가 정규화 책임(위치°/토크Nm/전류A/온도℃ — xMate 전축 JTS), 연속 실패 10회→Faulted, 재연결 시맨틱. capability로 **Pro 자동 판정**(전축 토크센서 쇼케이스).
- **카탈로그 미등록**: SDK 클라이언트가 없는 상태로 콤보에 노출하지 않음(빈 껍데기 금지 — JAKA 때 결정 준수). SDK 확보 → XCoreSdkStateClient → 등록 순.

### 검증
- 테스트 +4 (총 54): Pro 등급 판정, 폴링 프레임 정규화(토크 Nm 정규화 필드·VendorRaw 없음·미제공 null), 연속 실패→Faulted, Faulted 후 재연결.

---

## 2026-07-08 (4) — JAKA 온보딩 (2호 벤더 — §6 절차 첫 적용)

### 요약
- **§6 온보딩 체크리스트의 첫 실전 적용** — 하드웨어 무관 단계(1·2·3·5·6·7) 완료, 실기 단계(4·8)만 잔여.
- 온보딩 명제 실증: **코어·UI·수집기 코드 수정 0줄** — `Drivers.Jaka` 모듈 + 카탈로그 2항목(WPF/Collector)으로 끝. 벤더 격리 원칙 통과(§6 7단계).

### 채널 설계
- 1차 채널 = **모니터 스트림(TCP 10000)**: 컨트롤러가 상태 JSON을 ~10Hz 브로드캐스트 — 드라이버는 소켓 연결 후 수신만(명령 포트 10001 미사용). 구조적 비개입·무코딩.
- Modbus(502, 기본 ON)는 레지스터 맵 실기 검증 후 추가 — 추측 주소 구현 금지(두산 전례 준수).
- 등급: 전류/토크/온도가 펌웨어별 상이 → 후보 키(`instCurrent`/`joint_temp`/`torqsensor` 등)를 **VendorRaw로 보존**하고 `Has*=false` → capability 규약대로 **Basic 자동 판정**. 실기 확정 시 정규화+상향.

### 구현 (`Drivers/Jaka`)
- `JakaStreamFramer`: 중괄호 깊이 추적(문자열/이스케이프 인식) — TCP 분할·병합·앞쪽 쓰레기 바이트 안전.
- `JakaPacketParser`: 방어적 파싱(`data` 래핑 대응, 손상 패킷 스킵), 위치 필수·나머지 원시 보존. ⚠️ 위치 단위(°) 실기 대조 항목 있음(SDK는 라디안, 스트림은 도 문서화).
- `JakaJsonSource`: 재연결 시맨틱(무수신 5초/원격 종료 → Faulted), 접속 타임아웃 3초.
- `tools/JakaProbe`: 스트림 덤프 + 주기 실측 + 숫자 배열 필드 열거(★축별 후보 표시) + 파서 통과 확인 — §6 4단계 실기 매핑용.

### 검증
- 테스트 +6 (총 50): 프레이머(분할/병합/문자열 내 중괄호), 파서(정규화·VendorRaw 규약·래핑·손상), 등급 판정, **가짜 컨트롤러 TCP 서버 계약 테스트**(분할 송신 5프레임 수신 + 원격 종료 → Faulted 전이).

---

## 2026-07-08 (3) — Phase P5: 제품화 1차 (라이선스·웹 대시보드·무인 감시·패키징)

### 요약
- P5 3기둥 + 앞 단계에서 미뤄둔 Collector 통합 항목(P2 CBM·P3 ML·다중 로봇 뷰)을 한 번에 정리.
- Collector가 **무인 감시 노드**가 됨: 수집 + CBM/ML 판정 + 알림 저장 + 웹 대시보드 서빙.

### 작업 내용
- **라이선스** (`Core/Licensing`): RSA-SHA256 서명 `license.json` — payload 원문 바이트 서명(JSON 정규화 이슈 회피). 없음=평가판(전체 기능)/불량·만료=Basic 강등/정식=등급대로. 게이팅은 capability 자동 판정과 연동, WPF(카드)·Collector(채널 기동) 양쪽 강제. `tools/LicGen`(init/issue, 발급 즉시 자가 검증). ⚠️ dev-keys는 개발용 — 운영 키 교체 절차 주석 참조.
- **Collector 웹화**: Worker → ASP.NET Core(`Microsoft.NET.Sdk.Web`). `DashboardState`(채널 상태·5초 창 수집률·CBM/ML 스냅샷·알림 링 200) + `/api/status`·`/api/alerts` + `wwwroot` 다크 대시보드(1초 폴링, 의존성 제로). 기본 http://localhost:5100.
- **무인 감시**: 펌프마다 CbmMonitor + MlAnomalyMonitor 부착(WPF와 동일 파이프라인), 알림 → 대시보드 링 + `telemetry_alerts` 테이블(단건 insert, 수집 경로 비차단).
- **패키징**: `tools/package.ps1` — app/collector win-x64 self-contained zip (실측 80.5/51.3MB). `dist/` gitignore.

### 검증
- 테스트 +6 (총 44): 서명 왕복, 변조/타키/만료 거부, Trial/Basic 게이팅.
- 수집기 실기동: sim 2채널 수집(8/49Hz) + CBM Monitoring(건강도) + ML(스코어링 1,400윈도) → REST 응답 확인, headless Edge로 대시보드 렌더 캡처(카드 그리드 + 색상 알림 스트림).
- LicGen 발급 스모크: Pro/2027-12-31 발급 → Core 검증기 Licensed 판정.

### 메모
- 부트스트랩 ML 모델이 시뮬레이터 온도 사이클에 경계선 점수(-0.17 vs 임계 -0.157)로 간헐 Warning — 실 데이터 재학습 시 임계 재캘리브레이션 대상(이미 잔여 항목).

---

## 2026-07-08 (2) — Phase P4: 비전 진단 PoC (합성 씬)

### 요약
- 카메라 없이 비전 파이프라인 전체(촬영→검출→기준선→알림)를 검증 — 텔레메트리의 시뮬레이터와 같은 철학으로 **합성 마커 씬**을 만들어 구동.
- 순수 C#(외부 비전 라이브러리 없음): Otsu 이진화 + 최대 어두운 블롭 + 어두움 가중 무게중심(서브픽셀).

### 구조 (`BODA.CMS.Vision`)
- `MarkerDetector`: 서브픽셀 도트 검출. **대비 검증(클래스 평균차 ≥40)** — 무마커 단봉 장면에서 Otsu가 노이즈를 이분해 만드는 가짜 블롭 차단(테스트가 잡아낸 실결함). 블롭 면적 상·하한.
- `RepeatabilityMonitor`: 사이클 정지 촬영 관측 → 중심 x/y·지름 기준선(RunningStats 재사용) → z-판정. 위치 드리프트(양방향)=Alarm "비전 드리프트", 지름 감소(단방향)=Warning "마모 의심", 복귀 통지. **알림은 CbmAlert — 텔레메트리 CBM/ML과 채널 융합.**
- `SyntheticMarkerScene`: 안티앨리어싱 경계 + 재현 가능 노이즈 렌더러.
- `tools/VisionPoc`: 260사이클 시나리오(드리프트@120: +0.04px/cyc, 마모@160: −0.06px/cyc).

### 검증
- 테스트 +6 (총 38): 서브픽셀 정확도(<0.15px), 무마커 null, 드리프트/마모/증가 무시 판정, 엔드투엔드.
- PoC 실행: 검출률 260/260, 서브픽셀 오차 평균 0.030px(최대 0.075px, ≈1.5µm@0.05mm/px). 드리프트 주입 3~4사이클 만(이탈 0.022mm)에 Alarm, 마모 주입 직후 Warning. 오탐 0.

### 남은 것
- 실 카메라 연동(`IMarkerImageSource` 추상화 + 캘리브레이션 절차) — 카메라 확보 후. 검출기·감시기·합성 씬은 그대로 재사용.
- 피처 레벨 센서+비전 융합은 실 데이터 후.

---

## 2026-07-08 — Phase P3: ML 이상탐지 1차 (부트스트랩 모델)

### 요약
- 로드맵 AI 원칙(**Python 학습 → ONNX → .NET 추론**) 그대로 P3 1차 완료. CBM과 상호 보완: CBM은 단일 신호의 편차/추세, ML은 윈도의 *형태*(6피처 조합)를 본다.
- 현재 모델은 **합성 정상 데이터 부트스트랩** — 실 데이터가 TimescaleDB에 쌓이면 같은 스크립트로 재학습해 `models/`만 교체(코드 수정 불필요).

### 구조
- **피처(리샘플링 규약 해소)**: CBM 1초 집계의 z-정규화 값 → 슬라이딩 윈도(10) → [mean, std, rms, min, max, slope]. z-공간이라 신호·벤더·주기(1~100Hz) 무관 → **단일 모델**. ⚠️ 피처 정의는 C# `AnomalyFeatures` ↔ `tools/ml/train_anomaly.py` 완전 일치 유지.
- **학습**: IsolationForest(비지도, sklearn) — AR(1)+완만한 주기 성분의 합성 정상 z-시퀀스 76k 윈도. 임계 = 검증 정상 점수 0.5퍼센타일(오탐 ≈0.5%/윈도). skl2onnx export(`ai.onnx.ml` v3 고정 필요) + 사이드카 json(window/threshold). sklearn↔ONNX 점수 정합 오차 0 확인.
- **추론**: `OnnxAnomalyScorer`(ONNX Runtime 1.19.2, win-x64 네이티브 전이) + `MlAnomalyMonitor` — `CbmMonitor.AggregateEvaluated`(신규 이벤트, z 스트림) 구독, 임계 미달 연속 3회 → Warning, 정상 연속 5회 → 해제. 모델 없으면 ML만 비활성.
- **UI**: 카드 ML 칩 + 기존 CBM 알림 리스트 공유. 벤더 전환 시 ONNX 세션 해제(`Cleanup`).

### 검증
- 테스트 +3 (총 32): 피처 수치 정합, 스텁 스코어러로 디바운스/해제 판정, ONNX 모델 정상<->비정상 윈도 점수 분리(소프트 스킵 가드).
- 시뮬레이터 결함 주입 데모로 ML 알람 발화 확인(아래 스크린샷 절차와 동일).
- Python 환경: 3.14 + scikit-learn 1.9 / skl2onnx 1.20 / onnx 1.20 / onnxruntime 설치.

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
