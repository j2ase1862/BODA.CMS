using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BODA.CMS.Services
{
    public readonly record struct UpdateDownloadProgress(long BytesReceived, long TotalBytes);

    public sealed record UpdateDownloadResult(bool Success, string FilePath, string Error)
    {
        public static UpdateDownloadResult Ok(string path) => new(true, path, string.Empty);
        public static UpdateDownloadResult Fail(string error) => new(false, string.Empty, error);
    }

    public sealed record UpdateInstallLaunchResult(bool Success, bool UserDeclined, string Error, string LogPath);

    /// <summary>
    /// 인앱 업데이트 설치기 (2단계). 모니터 앱 MSI(BODA.CMS-app-*.msi)를 내려받아 SHA-256 을 검증하고,
    /// 관리자 권한(UAC) 부트스트랩 PowerShell 스크립트에 설치를 위임한다. 호출 측은 성공 반환 직후 앱을 종료해야 한다.
    ///
    /// 부트스트랩이 하는 일: ① 앱 프로세스 종료 대기(최대 60s, files-in-use 방지) ② msiexec /i /passive /norestart
    /// (MSI 로그 동봉) ③ 성공(0/3010)이면 MSI 삭제 ④ explorer.exe 경유로 앱 재실행(상승 권한 미상속)
    /// ⑤ 전 과정을 ProgramData\BODA\CMS\update-bootstrap.log 에 기록.
    ///
    /// 갱신 범위는 모니터 앱 MSI 뿐 — 감시 서버(Collector, Windows 서비스)는 별도 MSI 로 관리자가 올린다
    /// (다른 PC 에 있을 수 있고 서비스 중단을 동반하므로 앱이 임의로 건드리지 않는다).
    ///
    /// 검증용: 환경변수 BODA_CMS_UPDATE_DRYRUN=1 이면 UAC·msiexec 없이 부트스트랩을 돌려
    /// 다운로드→검증→종료 대기→재실행 경로만 재현한다.
    /// </summary>
    public sealed class UpdateInstallService : IDisposable
    {
        public const string DryRunEnv = "BODA_CMS_UPDATE_DRYRUN";
        private const int ErrorCancelled = 1223; // UAC 거부

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private bool _disposed;

        public string DownloadDir { get; }
        public string LogPath { get; }

        public UpdateInstallService() : this(null, null) { }

        /// <summary>테스트 친화 ctor — HttpClient / 다운로드 폴더 주입.</summary>
        public UpdateInstallService(HttpClient? httpClient, string? downloadDir)
        {
            _ownsHttp = httpClient is null;
            _http = httpClient ?? BuildClient();
            DownloadDir = downloadDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BODA", "CMS", "updates");
            LogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BODA", "CMS", "update-bootstrap.log");
        }

        private static HttpClient BuildClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; // 대용량·저속 회선 — 진행률로 사용자 확인
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BODA.CMS",
                GitHubUpdateService.ResolveCurrentVersion().ToString()));
            return client;
        }

        public static bool IsDryRun =>
            string.Equals(Environment.GetEnvironmentVariable(DryRunEnv), "1", StringComparison.Ordinal);

        /// <summary>
        /// MSI 를 스트리밍 다운로드(.part → 완료 시 rename)하고 SHA-256(제공 시)을 검증한다.
        /// 이미 검증된 동일 파일이 있으면 즉시 성공. 실패는 결과 객체로(취소는 OperationCanceledException 전파).
        /// </summary>
        public async Task<UpdateDownloadResult> DownloadAsync(UpdateInfo info,
            IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(info.DownloadUrl) || string.IsNullOrEmpty(info.DownloadFileName))
                return UpdateDownloadResult.Fail("릴리스에 모니터 앱 MSI(BODA.CMS-app-*.msi)가 없습니다.");
            if (!info.DownloadFileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                || info.DownloadFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return UpdateDownloadResult.Fail("자산 파일명이 올바르지 않습니다: " + info.DownloadFileName);

            try
            {
                Directory.CreateDirectory(DownloadDir);
                string target = Path.Combine(DownloadDir, info.DownloadFileName);
                string part = target + ".part";

                if (File.Exists(target) && Verify(target, info))
                {
                    progress?.Report(new UpdateDownloadProgress(new FileInfo(target).Length, new FileInfo(target).Length));
                    return UpdateDownloadResult.Ok(target);
                }
                File.Delete(part);

                using (HttpResponseMessage response = await _http.GetAsync(info.DownloadUrl,
                           HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return UpdateDownloadResult.Fail($"다운로드 실패: HTTP {(int)response.StatusCode}");

                    long total = response.Content.Headers.ContentLength ?? info.DownloadSizeBytes;
                    await using Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    {
                        var buffer = new byte[1 << 16];
                        long received = 0;
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            received += n;
                            progress?.Report(new UpdateDownloadProgress(received, total));
                        }
                    }
                }

                if (!Verify(part, info))
                {
                    File.Delete(part);
                    return UpdateDownloadResult.Fail("다운로드한 파일의 무결성 검증(SHA-256/크기)에 실패했습니다. 다시 시도해 주세요.");
                }
                File.Move(part, target, overwrite: true);
                return UpdateDownloadResult.Ok(target);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return UpdateDownloadResult.Fail("다운로드 실패: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>SHA-256 이 있으면 해시 일치, 없으면 크기 일치(크기도 없으면 존재)로 판정.</summary>
        internal static bool Verify(string path, UpdateInfo info)
        {
            if (!File.Exists(path)) return false;
            if (!string.IsNullOrEmpty(info.DownloadSha256))
                return string.Equals(ComputeSha256(path), info.DownloadSha256, StringComparison.OrdinalIgnoreCase);
            if (info.DownloadSizeBytes > 0) return new FileInfo(path).Length == info.DownloadSizeBytes;
            return new FileInfo(path).Length > 0;
        }

        internal static string ComputeSha256(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        /// <summary>
        /// 관리자 권한 부트스트랩을 띄운다. 성공 반환 직후 호출 측은 앱을 종료해야 설치가 진행된다.
        /// UAC 거부는 UserDeclined=true 로 구분.
        /// </summary>
        public UpdateInstallLaunchResult LaunchInstaller(string msiPath)
        {
            bool dryRun = IsDryRun;
            try
            {
                if (!File.Exists(msiPath)) return new(false, false, "MSI 파일이 없습니다: " + msiPath, LogPath);
                Directory.CreateDirectory(DownloadDir);
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                string scriptPath = Path.Combine(DownloadDir, "update-bootstrap.ps1");
                File.WriteAllText(scriptPath, BuildBootstrapScript(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BODA.CMS.exe");
                string args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                              $"-AppPid {Environment.ProcessId} -Msi \"{msiPath}\" -Exe \"{exe}\" -Log \"{LogPath}\"" +
                              (dryRun ? " -DryRun" : string.Empty);
                var psi = new ProcessStartInfo("powershell.exe", args)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Verb = dryRun ? string.Empty : "runas", // UAC 상승 — 거부 시 Win32Exception(1223)
                };
                Process.Start(psi);
                return new(true, false, string.Empty, LogPath);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new(false, true, "관리자 권한 승인이 취소되었습니다.", LogPath);
            }
            catch (Exception ex)
            {
                return new(false, false, "설치 프로그램 시작 실패: " + ex.GetBaseException().Message, LogPath);
            }
        }

        /// <summary>부트스트랩 스크립트 본문 — 인자로만 동작하는 고정 내용(경로 인젝션 없음).</summary>
        internal static string BuildBootstrapScript() => """
            param([int]$AppPid, [string]$Msi, [string]$Exe, [string]$Log, [switch]$DryRun)
            $ErrorActionPreference = 'Continue'
            function Write-Log([string]$m) {
                try { Add-Content -Path $Log -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $m) -Encoding UTF8 } catch {}
            }
            Write-Log ("bootstrap start: msi={0} exe={1} pid={2} dryrun={3}" -f $Msi, $Exe, $AppPid, $DryRun.IsPresent)

            # 1) 앱 종료 대기 — msiexec files-in-use 방지 (호출 측이 종료를 시작한 상태)
            $p = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
            if ($p) {
                Write-Log ("waiting for app exit (pid={0})" -f $AppPid)
                if (-not $p.WaitForExit(60000)) { Write-Log 'app still running after 60s - terminating'; Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue }
            }
            Start-Sleep -Seconds 1

            # 2) MSI 설치 (MajorUpgrade — 구버전 자동 제거). /passive: 진행률만, 질문 없음.
            $code = 0
            if ($DryRun) {
                Write-Log 'dry-run: msiexec skipped'
            } else {
                $msiLog = [System.IO.Path]::ChangeExtension($Log, '.msi.log')
                $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', ('"{0}"' -f $Msi), '/passive', '/norestart', '/l*v', ('"{0}"' -f $msiLog) -Wait -PassThru
                $code = $proc.ExitCode
                Write-Log ("msiexec exit={0} (log: {1})" -f $code, $msiLog)
            }
            $ok = ($code -eq 0 -or $code -eq 3010)   # 3010 = 재부팅 권장(정상 설치)

            # 3) 성공 시 MSI 정리
            if ($ok -and -not $DryRun) { Remove-Item $Msi -Force -ErrorAction SilentlyContinue; Write-Log 'msi removed' }
            elseif (-not $ok) { Write-Log 'install FAILED - previous version remains (MajorUpgrade rollback)' }

            # 4) 앱 재실행 — explorer.exe 경유로 상승 권한 미상속. 원 경로가 없으면 MSI 기본 설치 경로.
            $target = $Exe
            if (-not (Test-Path $target)) { $target = Join-Path $env:ProgramFiles 'BODA.CMS\BODA.CMS.exe' }
            if (Test-Path $target) {
                Write-Log ("relaunch: {0}" -f $target)
                Start-Process -FilePath 'explorer.exe' -ArgumentList ('"{0}"' -f $target)
            } else { Write-Log ("relaunch skipped - exe not found: {0}" -f $target) }
            Write-Log ("bootstrap end: ok={0}" -f $ok)
            """;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttp) _http.Dispose();
        }
    }
}
