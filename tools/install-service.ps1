# BODA.CMS.Collector Windows 서비스 설치/제거 (관리자 권한 필요)
#
# 사용:
#   설치(배포 폴더):  powershell -File tools\install-service.ps1 -ExePath "C:\BODA\collector\BODA.CMS.Collector.exe"
#   설치(개발 빌드):  powershell -File tools\install-service.ps1
#   제거:             powershell -File tools\install-service.ps1 -Uninstall
#
# 권장: tools\package.ps1 산출 zip을 고정 경로(예: C:\BODA\collector)에 푼 뒤 그 exe로 설치.
#       설정은 exe 옆 appsettings.json, 라이선스는 exe 옆 license.json.
param(
    [string]$ExePath,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$name = "BODA.CMS.Collector"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다 — PowerShell을 '관리자 권한으로 실행' 후 다시 시도하세요." -ForegroundColor Red
    exit 1
}

if ($Uninstall) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $svc) { Write-Host "서비스가 등록돼 있지 않습니다: $name"; exit 0 }
    if ($svc.Status -eq 'Running') { Write-Host "중지 중…"; Stop-Service -Name $name -Force }
    sc.exe delete $name | Out-Null
    Write-Host "제거 완료: $name" -ForegroundColor Green
    exit 0
}

if (-not $ExePath) {
    # 기본값: 저장소 개발 빌드 (운영은 -ExePath 로 배포 폴더 exe 지정 권장)
    $ExePath = Join-Path (Split-Path $PSScriptRoot -Parent) "Collector\bin\Debug\net8.0\BODA.CMS.Collector.exe"
}
$ExePath = (Resolve-Path $ExePath).Path
if (-not (Test-Path (Join-Path (Split-Path $ExePath) "appsettings.json"))) {
    Write-Host "경고: exe 옆에 appsettings.json 이 없습니다 — 로봇 구성이 비어 기동 후 대기만 합니다." -ForegroundColor Yellow
}

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "이미 등록돼 있습니다 — 먼저 -Uninstall 로 제거하세요." -ForegroundColor Red
    exit 1
}

# delayed-auto: 부팅 직후 네트워크가 뜨기 전 기동 실패를 피한다 (실패해도 재연결 백오프가 흡수).
sc.exe create $name binPath= "`"$ExePath`"" start= delayed-auto DisplayName= "BODA.CMS Collector" | Out-Null
sc.exe description $name "BODA.CMS 협동로봇 상태 수집·무인 감시 서비스 (웹 대시보드 포함)" | Out-Null
sc.exe failure $name reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null  # 비정상 종료 시 자동 재시작

Write-Host "설치 완료: $name → $ExePath" -ForegroundColor Green
Write-Host "시작 중…"
Start-Service -Name $name
Get-Service -Name $name | Format-Table Name, Status, StartType -AutoSize
Write-Host "대시보드: appsettings.json 의 Urls (기본 http://localhost:5100)"
Write-Host "로그: Windows 이벤트 뷰어 → 응용 프로그램 (원본: $name)"
