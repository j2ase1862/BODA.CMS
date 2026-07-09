# BODA.CMS 배포 패키징 (ROADMAP §4 P5)
# 사용: powershell -File tools\package.ps1 [-Version 0.5.0]
# 산출: dist\BODA.CMS-collector-setup-{v}-x64.exe (통합 설치 — PostgreSQL 동봉, 오프라인 단일 파일. 권장)
#       dist\BODA.CMS-app-{v}-x64.msi      (WPF 모니터 — 시작 메뉴·바탕화면 바로가기)
#       dist\BODA.CMS-collector-{v}-x64.msi (수집기+웹 단품 — DB 없이. Windows 서비스 자동 등록, C:\BODA\Collector)
#       dist\BODA.CMS-*-{v}-win-x64.zip     (xcopy 배포용 보조 — 파일 교체 업데이트·격리망)
# self-contained — 현장 PC에 .NET 설치 불필요. 라이선스(license.json)는 패키지에 포함하지 않는다(고객별 발급).
# MSI 빌드 도구: WiX 5 (dotnet tool). 없으면 자동 설치한다.
#   ⚠ WiX 6+ 는 상용 사용 시 OSMF(유지보수비) 동의가 필요하므로 5.0.x 로 고정한다.
param([string]$Version = "0.5.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root "dist\stage"
$dist = Join-Path $root "dist"

# ── WiX 준비 (최초 1회 자동 설치) ─────────────────────────────────────────────
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "WiX 5 설치 중 (dotnet tool)…"
    dotnet tool install --global wix --version 5.0.2
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
$wixExts = wix extension list -g
if (-not ($wixExts -match "WixToolset\.Util\.wixext")) { wix extension add -g WixToolset.Util.wixext/5.0.2 }
if (-not ($wixExts -match "WixToolset\.BootstrapperApplications\.wixext")) { wix extension add -g WixToolset.BootstrapperApplications.wixext/5.0.2 }

# 통합 설치 번들에 동봉할 PostgreSQL 설치본 (최초 1회 다운로드 후 dist\cache 에 캐시)
$pgInstaller = Join-Path $dist "cache\postgresql-16.4-1-windows-x64.exe"
if (-not (Test-Path $pgInstaller)) {
    Write-Host "== PostgreSQL 설치본 다운로드 (약 357MB, 최초 1회) =="
    New-Item -ItemType Directory -Force (Split-Path $pgInstaller) | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://get.enterprisedb.com/postgresql/postgresql-16.4-1-windows-x64.exe" `
        -OutFile $pgInstaller -UseBasicParsing
}

Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "== WPF 앱 publish (win-x64 self-contained, v$Version) =="
dotnet publish (Join-Path $root "BODA.CMS.csproj") -c Release -r win-x64 --self-contained `
    -p:Version=$Version -p:PublishSingleFile=false -o (Join-Path $stage "app") --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "앱 publish 실패" }

Write-Host "== Collector publish (win-x64 self-contained, v$Version) =="
dotnet publish (Join-Path $root "Collector\BODA.CMS.Collector.csproj") -c Release -r win-x64 --self-contained `
    -p:Version=$Version -o (Join-Path $stage "collector") --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Collector publish 실패" }

# 설치 스크립트 동봉 — DB 자동 구성(install-db.ps1)·zip 배포용 서비스 등록(install-service.ps1)
$colTools = Join-Path $stage "collector\tools"
New-Item -ItemType Directory -Force $colTools | Out-Null
Copy-Item (Join-Path $PSScriptRoot "install-service.ps1"), (Join-Path $PSScriptRoot "install-db.ps1") $colTools

# ── zip (xcopy 배포용 보조) ──────────────────────────────────────────────────
$appZip = Join-Path $dist "BODA.CMS-app-$Version-win-x64.zip"
$colZip = Join-Path $dist "BODA.CMS-collector-$Version-win-x64.zip"
Remove-Item $appZip, $colZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "app\*") -DestinationPath $appZip
Compress-Archive -Path (Join-Path $stage "collector\*") -DestinationPath $colZip

# ── MSI ──────────────────────────────────────────────────────────────────────
# Collector: exe(서비스 등록)·appsettings.json(현장 구성 보존)은 .wxs 에서 명시 컴포넌트로
# 다루므로 와일드카드 하베스팅 대상에서 분리해 둔다 (이중 포함 방지).
$colMain = Join-Path $stage "collector-main"
New-Item -ItemType Directory -Force $colMain | Out-Null
Move-Item (Join-Path $stage "collector\BODA.CMS.Collector.exe"), (Join-Path $stage "collector\appsettings.json") $colMain

$appMsi = Join-Path $dist "BODA.CMS-app-$Version-x64.msi"
$colMsi = Join-Path $dist "BODA.CMS-collector-$Version-x64.msi"
$appPayload = Join-Path $stage "app"
$colPayload = Join-Path $stage "collector"

Write-Host "== MSI 빌드 (모니터 앱) =="
wix build (Join-Path $root "installer\App.wxs") -arch x64 `
    -d "Version=$Version" -d "PayloadDir=$appPayload" -o $appMsi
if ($LASTEXITCODE -ne 0) { throw "앱 MSI 빌드 실패" }

Write-Host "== MSI 빌드 (감시 서버) =="
wix build (Join-Path $root "installer\Collector.wxs") -arch x64 -ext WixToolset.Util.wixext `
    -d "Version=$Version" -d "PayloadDir=$colPayload" -d "MainDir=$colMain" -o $colMsi
if ($LASTEXITCODE -ne 0) { throw "Collector MSI 빌드 실패" }

Write-Host "== 통합 설치 번들 빌드 (PostgreSQL 동봉) =="
$setupExe = Join-Path $dist "BODA.CMS-collector-setup-$Version-x64.exe"
wix build (Join-Path $root "installer\Bundle.wxs") `
    -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Util.wixext `
    -d "Version=$Version" -d "CollectorMsi=$colMsi" -d "PgInstaller=$pgInstaller" -o $setupExe
if ($LASTEXITCODE -ne 0) { throw "통합 설치 번들 빌드 실패" }

Remove-Item -Recurse -Force $stage

Write-Host "== 완료 =="
Get-Item $setupExe, $appMsi, $colMsi, $appZip, $colZip | ForEach-Object { "{0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
