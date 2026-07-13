using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Npgsql;

namespace BODA.CMS.Services
{
    /// <summary>telemetry_frames 인벤토리 1행 (로봇×채널의 수집 범위) — 재학습 구간 선택용.</summary>
    public sealed record DataInventoryRow(string RobotId, string Channel, DateTimeOffset MinTime, DateTimeOffset MaxTime, long Count)
    {
        public override string ToString() =>
            $"{RobotId} · {Channel}  —  {MinTime.LocalDateTime:yyyy-MM-dd HH:mm} ~ {MaxTime.LocalDateTime:MM-dd HH:mm}  ({Count:N0}건)";
    }

    /// <summary>재학습 요청 파라미터 (구간·필터는 tools\ml\retrain_anomaly.py 인자와 1:1).</summary>
    public sealed record RetrainRequest(
        string ConnectionString,
        DateTimeOffset Since,
        DateTimeOffset? Until,
        string? RobotId,
        string? Channel,
        int MinWindows,
        int LearningSeconds = 60); // 기준선 학습창(초) — Collector:Cbm 설정과 동일해야 정합

    /// <summary>
    /// 앱 내 이상탐지 모델 재학습 파이프라인 (P3 후속) — tools\retrain.ps1 의 UI 버전.
    ///
    /// 학습만 Python(sklearn IsolationForest → skl2onnx, 검증된 파이프라인 재사용), 나머지는 C#:
    ///   ① DB 인벤토리 조회(Npgsql) → ② 학습 실행(exe 옆 tools\ml\retrain_anomaly.py, 임시 스테이징 출력)
    ///   → ③ 모델 교체(앱 models\ + 설치된 Collector 서비스 models\, 기존은 backup-일시\ 로 보존).
    /// ③은 Program Files 쓰기·서비스 재시작이 필요하면 승격된 PowerShell(UAC 1회)로 수행한다.
    /// </summary>
    public sealed class ModelRetrainService
    {
        public const string CollectorServiceName = "BODA.CMS.Collector";
        private static readonly string[] ModelFiles = { "anomaly_iforest.onnx", "anomaly_iforest.json" };

        /// <summary>새 모델이 배포 전 생성되는 스테이징 폴더 (retrain.ps1 과 동일 위치).</summary>
        public static string StageDirectory => Path.Combine(Path.GetTempPath(), "boda-models-new");

        // ── ① DB 인벤토리: 로봇×채널별 수집 범위 ─────────────────────────────
        public async Task<IReadOnlyList<DataInventoryRow>> QueryInventoryAsync(string connectionString, CancellationToken ct)
        {
            var rows = new List<DataInventoryRow>();
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT robot_id, channel, min(time), max(time), count(*) FROM telemetry_frames GROUP BY 1, 2 ORDER BY 1, 2", conn);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                // timestamptz 는 UTC DateTime 으로 읽힌다 — 표시는 로컬 기준.
                rows.Add(new DataInventoryRow(
                    r.GetString(0), r.GetString(1),
                    new DateTimeOffset(r.GetDateTime(2)).ToLocalTime(),
                    new DateTimeOffset(r.GetDateTime(3)).ToLocalTime(),
                    r.GetInt64(4)));
            }
            return rows;
        }

        // ── ② 학습: Python 파이프라인 실행 → 스테이징 폴더에 새 모델 ─────────
        public async Task<bool> TrainAsync(RetrainRequest req, Action<string> log, CancellationToken ct)
        {
            string script = Path.Combine(AppContext.BaseDirectory, "tools", "ml", "retrain_anomaly.py");
            if (!File.Exists(script))
            {
                log($"재학습 스크립트가 없습니다: {script}");
                return false;
            }

            if (!await RunProcessAsync("python", "--version", log, ct, quiet: true))
            {
                log("Python 이 설치되어 있지 않습니다. 설치 후 다시 시도하세요:  winget install Python.Python.3.12");
                return false;
            }

            log("Python 패키지 확인 중 (최초 1회만 오래 걸립니다)…");
            if (!await RunProcessAsync("python",
                "-m pip install --quiet --disable-pip-version-check numpy scikit-learn skl2onnx psycopg2-binary onnxruntime",
                log, ct))
            {
                log("pip 패키지 설치 실패 — 인터넷 연결을 확인하세요.");
                return false;
            }

            if (Directory.Exists(StageDirectory)) Directory.Delete(StageDirectory, recursive: true);
            Directory.CreateDirectory(StageDirectory);

            var args = new StringBuilder();
            args.Append('"').Append(script).Append('"');
            args.Append(" --dsn \"").Append(ToLibpqDsn(req.ConnectionString)).Append('"');
            args.Append(" --since ").Append(req.Since.ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
            if (req.Until is DateTimeOffset until)
                args.Append(" --until ").Append(until.ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
            if (!string.IsNullOrWhiteSpace(req.RobotId)) args.Append(" --robot \"").Append(req.RobotId.Trim()).Append('"');
            if (!string.IsNullOrWhiteSpace(req.Channel)) args.Append(" --channel \"").Append(req.Channel.Trim()).Append('"');
            args.Append(" --min-windows ").Append(req.MinWindows);
            args.Append(" --learning-aggregates ").Append(req.LearningSeconds);
            args.Append(" --out \"").Append(StageDirectory).Append('"');

            log($"재학습 실행 — 구간 {req.Since.LocalDateTime:yyyy-MM-dd HH:mm} ~ {(req.Until?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "현재")}");
            if (!await RunProcessAsync("python", args.ToString(), log, ct))
            {
                log("재학습 실패 — 위 메시지 확인 (데이터 부족이면 구간을 늘리거나 최소 윈도를 낮추세요).");
                return false;
            }

            if (ModelFiles.Any(f => !File.Exists(Path.Combine(StageDirectory, f))))
            {
                log($"모델 산출물이 생성되지 않았습니다: {StageDirectory}");
                return false;
            }
            log($"새 모델 생성 완료: {StageDirectory}");
            return true;
        }

        // ── ③ 배포: 앱 models\ + Collector 서비스 models\ 교체(백업 포함) ────
        public async Task<bool> DeployAsync(Action<string> log)
        {
            string appModels = Path.Combine(AppContext.BaseDirectory, "models");
            string? collectorModels = FindCollectorModelsDir();

            var targets = new List<string> { appModels };
            if (collectorModels is not null &&
                !string.Equals(Path.GetFullPath(collectorModels), Path.GetFullPath(appModels), StringComparison.OrdinalIgnoreCase))
                targets.Add(collectorModels);

            // 서비스 재시작이 필요 없고 전 대상에 쓰기 가능하면 프로세스 안에서 바로 교체 (개발 환경 등).
            bool needsElevation = !IsElevated() && (collectorModels is not null || !targets.All(CanWriteDirectory));
            if (!needsElevation && collectorModels is null)
            {
                foreach (string dir in targets) CopyWithBackup(dir, log);
                return true;
            }

            return await DeployViaPowerShellAsync(targets, elevate: needsElevation, log);
        }

        private static void CopyWithBackup(string dir, Action<string> log)
        {
            Directory.CreateDirectory(dir);
            string[] old = Directory.GetFiles(dir, "anomaly_iforest.*");
            if (old.Length > 0)
            {
                string bak = Path.Combine(dir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(bak);
                foreach (string f in old) File.Move(f, Path.Combine(bak, Path.GetFileName(f)), overwrite: true);
            }
            foreach (string name in ModelFiles)
                File.Copy(Path.Combine(StageDirectory, name), Path.Combine(dir, name), overwrite: true);
            log($"모델 교체 완료: {dir}");
        }

        /// <summary>
        /// 승격 PowerShell 로 교체 — 서비스 중지 → 교체(백업) → 재시작. 승격 프로세스는 출력을
        /// 직접 받을 수 없어 스크립트가 로그 파일에 쓰고, 종료 후 읽어 UI 로그로 옮긴다.
        /// </summary>
        private static async Task<bool> DeployViaPowerShellAsync(IReadOnlyList<string> targets, bool elevate, Action<string> log)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "boda-retrain-deploy.log");
            string scriptPath = Path.Combine(Path.GetTempPath(), "boda-retrain-deploy.ps1");
            if (File.Exists(logPath)) File.Delete(logPath);

            string targetList = string.Join(",", targets.Select(t => $"'{t}'"));
            string script = $@"
$ErrorActionPreference = 'Stop'
function Log($m) {{ Add-Content -LiteralPath '{logPath}' -Value $m -Encoding UTF8 }}
try {{
    $svc = Get-Service -Name '{CollectorServiceName}' -ErrorAction SilentlyContinue
    $wasRunning = ($svc -ne $null) -and ($svc.Status -eq 'Running')
    if ($wasRunning) {{ Log 'Collector 서비스 중지'; Stop-Service -Name '{CollectorServiceName}' -Force }}
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($dir in @({targetList})) {{
        New-Item -ItemType Directory -Force $dir | Out-Null
        $old = Get-ChildItem $dir -Filter 'anomaly_iforest.*' -ErrorAction SilentlyContinue
        if ($old) {{
            $bak = Join-Path $dir ""backup-$stamp""
            New-Item -ItemType Directory -Force $bak | Out-Null
            $old | Move-Item -Destination $bak -Force
        }}
        Copy-Item '{Path.Combine(StageDirectory, ModelFiles[0])}','{Path.Combine(StageDirectory, ModelFiles[1])}' $dir -Force
        Log ""모델 교체 완료: $dir (기존은 backup-$stamp\)""
    }}
    if ($wasRunning) {{ Start-Service -Name '{CollectorServiceName}'; Log 'Collector 서비스 재시작 완료' }}
    exit 0
}}
catch {{ Log ('배포 실패: ' + $_.Exception.Message); exit 1 }}
";
            // PowerShell 5.1 은 BOM 없는 UTF-8 을 CP949 로 읽는다 — BOM 필수(한글 메시지 보존).
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) psi.Verb = "runas"; // UAC 1회 — Program Files 쓰기·서비스 재시작 때문

            int exitCode;
            try
            {
                using Process? proc = Process.Start(psi);
                if (proc is null) { log("배포 프로세스를 시작하지 못했습니다."); return false; }
                await proc.WaitForExitAsync();
                exitCode = proc.ExitCode;
            }
            catch (Win32Exception) // 사용자가 UAC 를 거부
            {
                log("관리자 권한 승인이 취소되어 모델을 교체하지 못했습니다.");
                log($"수동 적용: {StageDirectory} 의 두 파일을 각 models\\ 폴더에 복사한 뒤 서비스·앱을 재시작하세요.");
                return false;
            }

            if (File.Exists(logPath))
                foreach (string line in await File.ReadAllLinesAsync(logPath))
                    if (!string.IsNullOrWhiteSpace(line)) log(line);
            return exitCode == 0;
        }

        // ── 유틸 ──────────────────────────────────────────────────────────────
        /// <summary>설치된 Collector 서비스의 exe 옆 models\ (서비스 미설치면 null).</summary>
        private static string? FindCollectorModelsDir()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + CollectorServiceName);
            if (key?.GetValue("ImagePath") is not string image || string.IsNullOrWhiteSpace(image)) return null;
            image = image.Trim();
            string exe = image.StartsWith('"') ? image[1..image.IndexOf('"', 1)] : image.Split(' ')[0];
            string? dir = Path.GetDirectoryName(exe);
            return dir is null ? null : Path.Combine(dir, "models");
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool CanWriteDirectory(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Npgsql 접속 문자열 → psycopg2(libpq) DSN — retrain_anomaly.py --dsn 형식.</summary>
        internal static string ToLibpqDsn(string npgsqlConnectionString)
        {
            var b = new NpgsqlConnectionStringBuilder(npgsqlConnectionString);
            var parts = new List<string>
            {
                $"host={b.Host}", $"port={b.Port}", $"dbname={b.Database}", $"user={b.Username}",
            };
            if (!string.IsNullOrEmpty(b.Password)) parts.Add($"password={b.Password}");
            return string.Join(' ', parts);
        }

        private static async Task<bool> RunProcessAsync(string exe, string args, Action<string> log, CancellationToken ct, bool quiet = false)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // 콘솔 코드페이지(CP949)와 무관하게 파이썬 출력이 UTF-8 로 오게 — 한글 로그 깨짐 방지.
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (!quiet && !string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!quiet && !string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };

            try
            {
                if (!proc.Start()) return false;
            }
            catch (Exception ex)
            {
                if (!quiet) log($"{exe} 실행 실패: {ex.GetBaseException().Message}");
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // 취소 시 파이썬과 그 자식(pip 등)까지 종료.
            using CancellationTokenRegistration reg = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 이미 종료 */ }
            });
            await proc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return proc.ExitCode == 0;
        }
    }
}
