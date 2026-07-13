# BODA.CMS 이상탐지 모델 재학습 원클릭 스크립트 (현장 PC용)
# 데이터가 하루 이상 쌓인 뒤 실행하면: 패키지 설치 → 재학습(tools\ml\retrain_anomaly.py)
# → 새 모델 검증 → 설치된 Collector 서비스·모니터 앱의 models\ 교체(백업 포함) → 서비스 재시작.
#
# 사용 (관리자 PowerShell — 서비스 재시작·Program Files 쓰기 때문):
#   powershell -ExecutionPolicy Bypass -File tools\retrain.ps1                    # 최근 24시간으로 학습
#   powershell -File tools\retrain.ps1 -Since 2026-07-10T09:00 -Robot doosan-01  # 구간·로봇 지정
#   powershell -File tools\retrain.ps1 -SkipDeploy                                # 모델만 생성(교체 안 함)
#
# ⚠ -Since/-Until 구간은 로봇이 "정상 운전"하던 시간대여야 한다.
#   실제 충돌·비정상 동작이 있던 시간대가 섞이면 그 패턴까지 정상으로 학습된다.
param(
    [string]$Since,                     # 학습 시작 시각 (기본: 24시간 전)
    [string]$Until,                     # 학습 종료 시각 (기본: 현재)
    [string]$Robot,                     # 특정 robot_id 만 (기본: 전체)
    [string]$Channel,                   # 특정 채널만 (drfl/modbus)
    [string]$Dsn = "host=localhost port=5432 dbname=boda_cms user=postgres password=postgres",
    [int]$MinWindows = 5000,            # 실 데이터 윈도 최소 개수 (미달 시 중단)
    [int]$LearningSeconds = 0,          # 기준선 학습창(초) — Collector:Cbm 설정과 동일 값 (0=기본 60)
    [double]$SyntheticFrac = 0.25,      # 합성 정상 blend 비율
    [switch]$SkipDeploy,                # 모델 생성만 하고 교체·재시작 생략
    [string]$PythonExe = "python"
)
$ErrorActionPreference = "Stop"

$pyScript = Join-Path $PSScriptRoot "ml\retrain_anomaly.py"
if (-not (Test-Path $pyScript)) { throw "재학습 스크립트가 없습니다: $pyScript" }

# ── Python 준비 ───────────────────────────────────────────────────────────────
if (-not (Get-Command $PythonExe -ErrorAction SilentlyContinue)) {
    throw "Python 이 없습니다. 설치 후 다시 실행하세요:  winget install Python.Python.3.12"
}
Write-Host "== Python 패키지 확인 (최초 1회만 오래 걸림) =="
& $PythonExe -m pip install --quiet --disable-pip-version-check `
    numpy scikit-learn skl2onnx psycopg2-binary onnxruntime
if ($LASTEXITCODE -ne 0) { throw "pip 패키지 설치 실패" }

# ── 재학습 → 스테이징 폴더에 새 모델 생성 ─────────────────────────────────────
if (-not $Since) { $Since = (Get-Date).AddDays(-1).ToString("yyyy-MM-ddTHH:mm:sszzz") }
$stage = Join-Path $env:TEMP "boda-models-new"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

$pyArgs = @($pyScript, "--dsn", $Dsn, "--since", $Since, "--out", $stage,
            "--min-windows", $MinWindows, "--synthetic-frac", $SyntheticFrac)
if ($Until)   { $pyArgs += @("--until", $Until) }
if ($Robot)   { $pyArgs += @("--robot", $Robot) }
if ($Channel) { $pyArgs += @("--channel", $Channel) }
if ($LearningSeconds -gt 0) { $pyArgs += @("--learning-aggregates", $LearningSeconds) }

Write-Host "== 재학습 실행 (구간: $Since ~ $(if ($Until) { $Until } else { '현재' })) =="
& $PythonExe @pyArgs
if ($LASTEXITCODE -ne 0) { throw "재학습 실패 — 위 메시지 확인 (데이터 부족이면 구간을 늘리거나 -MinWindows 를 낮추세요)" }

$onnx = Join-Path $stage "anomaly_iforest.onnx"
$json = Join-Path $stage "anomaly_iforest.json"
if (-not (Test-Path $onnx) -or -not (Test-Path $json)) { throw "모델 산출물이 없습니다: $stage" }
Write-Host "새 모델 생성 완료: $stage"

if ($SkipDeploy) {
    Write-Host "`n-SkipDeploy — 교체 생략. 수동 적용: 위 두 파일을 exe 옆 models\ 에 복사 후 재시작."
    return
}

# ── 배포 대상 탐색: Collector 서비스(exe 경로에서 역추적) + 모니터 앱 설치 폴더 ──
$targets = @()

$svc = Get-CimInstance Win32_Service -Filter "Name='BODA.CMS.Collector'" -ErrorAction SilentlyContinue
if ($svc) {
    # PathName 은 따옴표가 붙을 수 있음: "C:\BODA\Collector\BODA.CMS.Collector.exe"
    $svcExe = if ($svc.PathName -match '^"([^"]+)"') { $Matches[1] } else { ($svc.PathName -split ' ')[0] }
    $targets += @{ Name = "Collector 서비스"; Dir = Join-Path (Split-Path $svcExe -Parent) "models"; Service = $svc }
}

$appDir = "C:\Program Files\BODA.CMS"
if (Test-Path (Join-Path $appDir "BODA.CMS.exe")) {
    $targets += @{ Name = "모니터 앱"; Dir = Join-Path $appDir "models"; Service = $null }
}

# 개발 환경(저장소에서 실행)이면 저장소 models\ 도 갱신 — 이후 빌드에 새 모델이 실린다
$repoRoot = Split-Path $PSScriptRoot -Parent
if (Test-Path (Join-Path $repoRoot "BODA.CMS.csproj")) {
    $targets += @{ Name = "저장소 models\"; Dir = Join-Path $repoRoot "models"; Service = $null }
}

if (-not $targets) {
    Write-Warning "설치된 Collector 서비스·모니터 앱을 찾지 못했습니다. 수동 적용: $stage 의 두 파일을 exe 옆 models\ 에 복사 후 재시작."
    return
}

# ── 교체(기존 모델 백업) + 서비스 재시작 ─────────────────────────────────────
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
foreach ($t in $targets) {
    $svcWasRunning = $false
    if ($t.Service -and $t.Service.State -eq "Running") {
        Write-Host "== $($t.Name) 중지 =="
        Stop-Service -Name $t.Service.Name -Force
        $svcWasRunning = $true
    }

    New-Item -ItemType Directory -Force $t.Dir | Out-Null
    $old = Get-ChildItem $t.Dir -Filter "anomaly_iforest.*" -ErrorAction SilentlyContinue
    if ($old) {
        $bak = Join-Path $t.Dir "backup-$stamp"
        New-Item -ItemType Directory -Force $bak | Out-Null
        $old | Move-Item -Destination $bak
    }
    Copy-Item $onnx, $json $t.Dir
    Write-Host "$($t.Name): 모델 교체 완료 → $($t.Dir)$(if ($old) { " (기존은 backup-$stamp\)" })"

    if ($svcWasRunning) {
        Start-Service -Name $t.Service.Name
        Write-Host "$($t.Name) 재시작 완료."
    }
}

Write-Host "`n== 완료 =="
Write-Host "모니터 앱이 실행 중이면 껐다 켜야 새 모델이 반영됩니다."
Write-Host "적용 확인: 대시보드/앱에서 'ML 이상' 알람 빈도가 줄었는지 하루 정도 지켜보세요."
