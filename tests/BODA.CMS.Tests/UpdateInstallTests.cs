using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using BODA.CMS.Services;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>인앱 업데이트 설치기(2단계) — 다운로드·SHA-256 검증·재사용·부트스트랩 스크립트 내용.</summary>
    public class UpdateInstallTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "boda-cms-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 정리 실패는 무시 */ }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public int Calls;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(_respond(request));
            }
        }

        private static byte[] Payload(int size = 300_000)
        {
            var rnd = new Random(42);
            var b = new byte[size];
            rnd.NextBytes(b);
            return b;
        }

        private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        private static UpdateInfo Info(byte[] payload, string? sha, string name = "BODA.CMS-app-9.9.9-x64.msi") => new()
        {
            CurrentVersion = new Version(0, 7, 4),
            LatestVersion = new Version(9, 9, 9),
            LatestTagName = "v9.9.9",
            DownloadUrl = "https://x/app.msi",
            DownloadFileName = name,
            DownloadSizeBytes = payload.Length,
            DownloadSha256 = sha ?? string.Empty,
        };

        [Fact]
        public async Task Download_VerifiesSha_ReportsProgress_AndReusesFile()
        {
            byte[] payload = Payload();
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            using var svc = new UpdateInstallService(new HttpClient(handler), _dir);
            UpdateInfo info = Info(payload, Sha(payload));

            var reports = new List<UpdateDownloadProgress>();
            var progress = new SynchronousProgress(reports.Add);
            UpdateDownloadResult r = await svc.DownloadAsync(info, progress);

            Assert.True(r.Success, r.Error);
            Assert.Equal(Path.Combine(_dir, info.DownloadFileName), r.FilePath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(r.FilePath));
            Assert.False(File.Exists(r.FilePath + ".part"));
            Assert.NotEmpty(reports);
            Assert.Equal(payload.Length, reports[^1].BytesReceived);
            Assert.Equal(payload.Length, reports[^1].TotalBytes);

            // 검증된 동일 파일 → 재다운로드 없음
            UpdateDownloadResult again = await svc.DownloadAsync(info);
            Assert.True(again.Success);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task Download_ShaMismatch_FailsAndDeletesFile()
        {
            byte[] payload = Payload();
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            using var svc = new UpdateInstallService(new HttpClient(handler), _dir);
            UpdateInfo info = Info(payload, new string('a', 64));

            UpdateDownloadResult r = await svc.DownloadAsync(info);

            Assert.False(r.Success);
            Assert.Contains("무결성", r.Error);
            Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>());
        }

        [Fact]
        public async Task Download_NoSha_FallsBackToSizeCheck()
        {
            byte[] payload = Payload(1000);
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            using var svc = new UpdateInstallService(new HttpClient(handler), _dir);

            Assert.True((await svc.DownloadAsync(Info(payload, null))).Success);

            // 크기 불일치 → 실패
            UpdateInfo wrongSize = Info(payload, null) with { DownloadSizeBytes = 999, DownloadFileName = "BODA.CMS-app-1.0.0-x64.msi" };
            Assert.False((await svc.DownloadAsync(wrongSize)).Success);
        }

        [Fact]
        public async Task Download_HttpError_ReturnsFailure()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var svc = new UpdateInstallService(new HttpClient(handler), _dir);
            UpdateDownloadResult r = await svc.DownloadAsync(Info(Payload(10), null));
            Assert.False(r.Success);
            Assert.Contains("404", r.Error);
        }

        [Theory]
        [InlineData("", "https://x/app.msi")]                 // 파일명 없음
        [InlineData("BODA.CMS-app-1.0.0-x64.msi", "")]        // URL 없음
        [InlineData("..\\evil.msi", "https://x/app.msi")]     // 경로 문자
        [InlineData("app.exe", "https://x/app.msi")]          // msi 아님
        public async Task Download_RejectsBadAsset(string name, string url)
        {
            using var svc = new UpdateInstallService(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), _dir);
            UpdateInfo info = Info(Payload(10), null, name: name.Length > 0 ? name : "") with { DownloadUrl = url };
            UpdateDownloadResult r = await svc.DownloadAsync(info);
            Assert.False(r.Success);
        }

        [Fact]
        public async Task Download_Cancel_Propagates()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload()) });
            using var svc = new UpdateInstallService(new HttpClient(handler), _dir);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.DownloadAsync(Info(Payload(), null), null, cts.Token));
        }

        [Fact]
        public void LaunchInstaller_MissingMsi_Fails()
        {
            using var svc = new UpdateInstallService(new HttpClient(new StubHandler(_ => new HttpResponseMessage())), _dir);
            UpdateInstallLaunchResult r = svc.LaunchInstaller(Path.Combine(_dir, "nope.msi"));
            Assert.False(r.Success);
            Assert.False(r.UserDeclined);
        }

        [Fact]
        public void BootstrapScript_HasInstallWaitRelaunchAndLogging()
        {
            string s = UpdateInstallService.BuildBootstrapScript();
            Assert.Contains("param([int]$AppPid, [string]$Msi, [string]$Exe, [string]$Log, [switch]$DryRun)", s);
            Assert.Contains("WaitForExit(60000)", s);                       // 앱 종료 대기
            Assert.Contains("'msiexec.exe'", s);
            Assert.Contains("'/passive', '/norestart'", s);                 // 질문 없는 설치
            Assert.Contains("$code -eq 0 -or $code -eq 3010", s);          // 3010 = 재부팅 권장도 성공
            Assert.Contains("'explorer.exe'", s);                          // 상승 권한 미상속 재실행
            Assert.Contains("BODA.CMS\\BODA.CMS.exe", s);                   // MSI 기본 경로 폴백
            Assert.Contains("dry-run: msiexec skipped", s);
            Assert.DoesNotContain("Invoke-Expression", s);
        }

        /// <summary>테스트용 동기 Progress — Progress&lt;T&gt; 는 SynchronizationContext 로 포스팅해 타이밍이 흔들린다.</summary>
        private sealed class SynchronousProgress : IProgress<UpdateDownloadProgress>
        {
            private readonly Action<UpdateDownloadProgress> _handler;
            public SynchronousProgress(Action<UpdateDownloadProgress> handler) => _handler = handler;
            public void Report(UpdateDownloadProgress value) => _handler(value);
        }
    }
}
