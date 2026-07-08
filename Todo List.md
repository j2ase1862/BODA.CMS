# Todo List — 남은 작업 (2026-07-08 기준)

> 이 파일은 **작업용 추출본**입니다. 프로젝트의 단일 기준(source of truth)은 [`ROADMAP.md`](ROADMAP.md)이며,
> 항목 완료 시 이 파일과 ROADMAP의 해당 체크박스를 함께 갱신하세요.
> P0~P5 전 페이즈의 1차 구현은 완료 — 남은 작업은 대부분 **외부 확보물**(🔶 표시)에 걸려 있습니다.

---

## 1. Rokae (3호 벤더 — 드라이버 본체 완료, §5.3)

- [ ] 🔶 **xCoreSDK-CSharp 바이너리 확보** — [GitHub Releases](https://github.com/RokaeRobot/xCoreSDK-CSharp)에서
      `xCoreSDK-CSharp-{version}-win.zip` 다운로드 → `libs/rokae/`에 번들 (Apache-2.0, 두산 DRFL 전례)
- [ ] `XCoreSdkStateClient` 구현 — `IRokaeStateClient`의 SDK 래핑 구현체
      (상태 조회 API만 사용 — 모션 명령·제어권·RCI 금지. 단위 환산 책임: SDK가 라디안이면 여기서 °로)
- [ ] 카탈로그 등록 — `MainWindow.xaml.cs` + `Collector/Program.cs` 각 1항목
      (클라이언트 없는 빈 껍데기 노출 금지 원칙 — 반드시 위 두 항목 완료 후)
- [ ] 🔶 실기 확보 → 프로브/실기 매핑(§6 3·4단계): 조회 주기 상한 실측, 위치 단위 대조,
      전축 토크센서 값 sanity → capability 검증(Pro 판정 확정)
- [ ] 실측·펌웨어 호환성 노트 기록 (ROADMAP §5.3, §6 8단계)

## 2. JAKA (2호 벤더 — 스트림 드라이버 완료, §5.2)

- [ ] 🔶 실기 확보 → `tools/JakaProbe`로 실기 매핑(§6 4단계):
  - [ ] 모니터 스트림 주기 실측 (공칭 10Hz 검증)
  - [ ] `joint_actual_position` 단위 대조 (도° 여부 — 펜던트 비교)
  - [ ] 전류/온도/토크 필드명·단위 확정 → 정규화 매핑 + capability 상향(Pro 승격 검토)
- [ ] 🔶 Modbus 레지스터 맵 실기 검증 → `JakaModbusSource` 추가 (추측 주소 구현 금지)
- [ ] 실측 맵·펌웨어 호환성 노트 기록 (§6 8단계)

## 3. 두산 (1호 레퍼런스 — §5.1)

- [ ] 🔶 **DRCF 2.11 매칭 v2 DRFL DLL 확보** — ⓐ Doosan Robot Lab 로그인 → 이전 버전 다운로드,
      또는 ⓑ 컨트롤러 짝 DART-Studio 설치 폴더에서 복사 → `libs/x64_v2/` 교체 (§5.1 검증 노트)
- [ ] 🔶 DRFL 실기 검증 — 콜백 수신·`GetSystemVersion` 정합·축별 값 sanity (D-Phase 2 종료 게이트)
- [ ] 🔶 Modbus 온도(300~305)·전류/토크(400~405) 스케일 확정 (조그로 비영 값 확보 후 펜던트 대조)
      → `DoosanModbusSource` 정규화 승격 + capability 상향
- [ ] 🔶 J2~J6 개별 조그로 축 순서 확정, 속도 레지스터 탐색
- [ ] (선택) RT 1kHz 오프라인 캐릭터라이제이션 — 건강 기준 시그니처 정밀 측정 (⚠️ 라이브 금지)

## 4. 데이터 파이프라인 (P1 잔여)

- [ ] 🔶 **TimescaleDB 인스턴스 확보** (도커 or 기존 BODA 스택) → `Storage:Enabled=true` 실 적재 검증
      (`telemetry_frames` COPY 배치 + `telemetry_alerts` 단건 — 코드는 완료, 실 DB 미검증)

## 5. CBM / ML (P2·P3 잔여)

- [ ] 기준선 영속화 — 재시작 시 재학습 생략 (저장 위치: DB or 로컬 파일)
- [ ] 운전 조건별(프로그램/모드) 기준선 분리 — 실 데이터 확보 후
- [ ] ML 실 데이터 재학습 파이프라인 — TimescaleDB → 피처 추출 → `tools/ml/train_anomaly.py` 재학습
      → `models/` 교체 (부트스트랩 모델의 경계선 오탐(임계 -0.157 부근) 재캘리브레이션 포함)

## 6. 비전 (P4 잔여)

- [ ] 🔶 실 카메라 확보 → `IMarkerImageSource` 추상화 + 캡처 SDK 연동 + px→mm 캘리브레이션 절차
      (검출기·감시기·합성 씬은 그대로 재사용)
- [ ] 피처 레벨 센서+비전 융합 진단 — 실 데이터 축적 후

## 7. 제품화 (P5 잔여)

- [ ] 운영 라이선스 서명키 발급·보안 보관 → `Core.Licensing.LicenseVerifier.DevPublicKeyPem` 교체
      (현재 저장소의 dev-keys는 개발용)
- [ ] 알림 통보 채널 (메일/메신저 webhook) — 고객 요구 시
- [ ] 자동 업데이트 채널·드라이버 모듈 단위 배포 — 고객 배포 시점에
- [ ] Blazor/SignalR 실시간 대시보드 + 사용자 인증 — 다중 고객 SaaS화 시점에
- [ ] 후보 벤더 조사 (UR RTDE·Techman 등) — 시장 요구 발생 시 §6 절차로

---

**범례**: 🔶 = 외부 확보물(하드웨어·바이너리·인프라) 필요 — 소프트웨어만으로 진행 불가.
