# BODA.CMS 데이터 저장용 PostgreSQL 자동 설치 (관리자 권한 필요)
#
# 사용:
#   기본(인터넷 되는 PC):  powershell -File tools\install-db.ps1
#   오프라인 PC:           powershell -File tools\install-db.ps1 -InstallerPath "D:\postgresql-16.4-1-windows-x64.exe"
#   비밀번호 직접 지정:    powershell -File tools\install-db.ps1 -Password "원하는비밀번호"
#
# 하는 일 (전부 자동):
#   1) PostgreSQL 무인 설치 — 이미 설치돼 있으면 건너뛰고 재사용
#   2) boda_cms 데이터베이스 생성 (+ TimescaleDB 확장이 있으면 활성화)
#   3) 스크립트 상위 폴더 appsettings.json 에 접속 문자열 기록 + Storage 켜기
#   4) BODA.CMS.Collector 서비스가 등록돼 있으면 재시작
#
# 주의: 이미 설치된 PostgreSQL을 재사용할 때는 그 서버의 postgres 비밀번호를
#       -Password 로 반드시 넘겨야 합니다(모르면 접속할 수 없습니다).
param(
    [string]$Password,               # 미지정 시 무작위 생성 — 완료 메시지와 appsettings.json에서 확인
    [int]$Port = 5432,
    [string]$DbName = "boda_cms",
    [string]$PgVersion = "16.4-1",   # EDB 설치본 버전 (다운로드 URL 구성에 사용)
    [string]$InstallerPath,          # 오프라인: 미리 받아둔 EDB 설치본 exe 경로
    [string]$AppSettings             # 기본: 스크립트 상위 폴더의 appsettings.json
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다 — PowerShell을 '관리자 권한으로 실행' 후 다시 시도하세요." -ForegroundColor Red
    exit 1
}

if (-not $AppSettings) { $AppSettings = Join-Path (Split-Path $PSScriptRoot -Parent) "appsettings.json" }
$pgMajor = ($PgVersion -split '\.')[0]

# ── 1) PostgreSQL 설치 (이미 있으면 재사용) ─────────────────────────────────
$pgSvc = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pgSvc) {
    Write-Host "PostgreSQL이 이미 설치돼 있습니다 ($($pgSvc.Name)) — 설치를 건너뜁니다."
    if (-not $Password) {
        Write-Host "기존 서버를 재사용하려면 그 서버의 postgres 비밀번호를 -Password 로 넘겨주세요." -ForegroundColor Red
        exit 1
    }
    if ($pgSvc.Status -ne 'Running') { Start-Service -Name $pgSvc.Name }
}
else {
    if (-not $Password) {
        $chars = [char[]]([char]'A'..[char]'Z') + [char[]]([char]'a'..[char]'z') + [char[]]([char]'0'..[char]'9')
        $Password = -join ($chars | Get-Random -Count 16)
    }

    if (-not $InstallerPath) {
        $url = "https://get.enterprisedb.com/postgresql/postgresql-$PgVersion-windows-x64.exe"
        $InstallerPath = Join-Path $env:TEMP "postgresql-$PgVersion-windows-x64.exe"
        if (-not (Test-Path $InstallerPath)) {
            Write-Host "PostgreSQL 설치본 다운로드 중 (약 350MB)… $url"
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            try {
                Invoke-WebRequest -Uri $url -OutFile $InstallerPath -UseBasicParsing
            }
            catch {
                Write-Host "다운로드 실패 — 인터넷이 안 되는 PC라면 다른 PC에서 아래 주소로 받아 -InstallerPath 로 지정하세요." -ForegroundColor Red
                Write-Host "  $url" -ForegroundColor Yellow
                exit 1
            }
        }
        else { Write-Host "받아둔 설치본 재사용: $InstallerPath" }
    }
    $InstallerPath = (Resolve-Path $InstallerPath).Path

    Write-Host "PostgreSQL $pgMajor 무인 설치 중 (수 분 소요, 창이 뜨지 않습니다)…"
    $proc = Start-Process -FilePath $InstallerPath -Wait -PassThru -ArgumentList @(
        "--mode", "unattended",
        "--unattendedmodeui", "none",
        "--superaccount", "postgres",
        "--superpassword", $Password,
        "--serverport", "$Port",
        "--servicename", "postgresql-x64-$pgMajor",
        "--disable-components", "stackbuilder"
    )
    if ($proc.ExitCode -ne 0) {
        Write-Host "설치 실패 (코드 $($proc.ExitCode)) — 로그: %TEMP%\install-postgresql.log 또는 bitrock_installer*.log" -ForegroundColor Red
        exit 1
    }
    Write-Host "PostgreSQL 설치 완료." -ForegroundColor Green
}

# psql 경로 — 표준 설치 위치에서 가장 최신 버전을 찾는다.
$psql = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $psql) {
    Write-Host "psql.exe 를 찾지 못했습니다 — PostgreSQL이 표준 경로(C:\Program Files\PostgreSQL)에 있는지 확인하세요." -ForegroundColor Red
    exit 1
}

# ── 2) 데이터베이스 생성 ─────────────────────────────────────────────────────
$env:PGPASSWORD = $Password
$exists = & $psql -U postgres -h localhost -p $Port -tAc "SELECT 1 FROM pg_database WHERE datname='$DbName'"
if ($LASTEXITCODE -ne 0) {
    Write-Host "DB 접속 실패 — 비밀번호가 맞는지, 서비스가 실행 중인지 확인하세요." -ForegroundColor Red
    exit 1
}
if ($exists -ne "1") {
    & $psql -U postgres -h localhost -p $Port -c "CREATE DATABASE $DbName" | Out-Null
    Write-Host "데이터베이스 생성: $DbName" -ForegroundColor Green
}
else { Write-Host "데이터베이스가 이미 있습니다: $DbName" }

# TimescaleDB 확장이 서버에 깔려 있으면 활성화 (없어도 일반 테이블로 동작하므로 통과).
$tsAvail = & $psql -U postgres -h localhost -p $Port -tAc "SELECT 1 FROM pg_available_extensions WHERE name='timescaledb'"
if ($tsAvail -eq "1") {
    & $psql -U postgres -h localhost -p $Port -d $DbName -c "CREATE EXTENSION IF NOT EXISTS timescaledb" | Out-Null
    Write-Host "TimescaleDB 확장 활성화 완료."
}
else { Write-Host "TimescaleDB 확장 없음 — 일반 테이블로 동작합니다 (감시 기능에는 지장 없음)." }
Remove-Item Env:\PGPASSWORD

# ── 3) appsettings.json 에 접속 정보 기록 ────────────────────────────────────
$connStr = "Host=localhost;Port=$Port;Database=$DbName;Username=postgres;Password=$Password"
if (Test-Path $AppSettings) {
    $raw = Get-Content $AppSettings -Raw
    $newRaw = $raw -replace '"ConnectionString"\s*:\s*"[^"]*"', "`"ConnectionString`": `"$connStr`""
    $newRaw = $newRaw -replace '"Enabled"\s*:\s*false', '"Enabled": true'
    if ($newRaw -ne $raw) {
        Copy-Item $AppSettings "$AppSettings.bak" -Force
        Set-Content -Path $AppSettings -Value $newRaw -Encoding UTF8 -NoNewline
        Write-Host "설정 기록 완료: $AppSettings (원본은 .bak 로 보관)" -ForegroundColor Green
    }
    elseif ($raw -match [regex]::Escape($connStr)) { Write-Host "설정이 이미 최신입니다: $AppSettings" }
    else {
        Write-Host "appsettings.json 에서 Storage 항목을 찾지 못했습니다 — 아래를 Collector 섹션에 직접 넣어주세요:" -ForegroundColor Yellow
        Write-Host "  `"Storage`": { `"Enabled`": true, `"ConnectionString`": `"$connStr`" }"
    }
}
else {
    Write-Host "appsettings.json 이 없습니다($AppSettings) — 감시 서버 폴더에서 실행했는지 확인하세요." -ForegroundColor Yellow
    Write-Host "접속 문자열: $connStr"
}

# ── 4) 감시 서버 재시작 ──────────────────────────────────────────────────────
$col = Get-Service -Name "BODA.CMS.Collector" -ErrorAction SilentlyContinue
if ($col) {
    Write-Host "감시 서버 재시작 중…"
    Restart-Service -Name "BODA.CMS.Collector" -Force
}

Write-Host ""
Write-Host "== 완료 ==" -ForegroundColor Green
Write-Host "DB           : $DbName (localhost:$Port)"
Write-Host "postgres 암호: $Password  ← appsettings.json 에도 저장돼 있습니다"
Write-Host "테이블은 감시 서버가 첫 시작 때 자동 생성합니다. 대시보드(http://localhost:5100)에서 수집 확인."
