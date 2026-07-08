# BODA.CMS 배포 패키징 (ROADMAP §4 P5)
# 사용: powershell -File tools\package.ps1 [-Version 0.5.0]
# 산출: dist\BODA.CMS-app-{v}-win-x64.zip (WPF 모니터), dist\BODA.CMS-collector-{v}-win-x64.zip (수집기+웹)
# self-contained — 현장 PC에 .NET 설치 불필요. 라이선스(license.json)는 zip에 포함하지 않는다(고객별 발급).
param([string]$Version = "0.5.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root "dist\stage"
$dist = Join-Path $root "dist"

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

$appZip = Join-Path $dist "BODA.CMS-app-$Version-win-x64.zip"
$colZip = Join-Path $dist "BODA.CMS-collector-$Version-win-x64.zip"
Remove-Item $appZip, $colZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "app\*") -DestinationPath $appZip
Compress-Archive -Path (Join-Path $stage "collector\*") -DestinationPath $colZip
Remove-Item -Recurse -Force $stage

Write-Host "== 완료 =="
Get-Item $appZip, $colZip | ForEach-Object { "{0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
