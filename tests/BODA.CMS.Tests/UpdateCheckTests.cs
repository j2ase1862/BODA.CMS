using System.Net;
using System.Net.Http;
using BODA.CMS.Services;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>인앱 업데이트 알림 체커(1단계) — 태그 파싱·앱 MSI 자산 선택·best-effort 실패 처리.</summary>
    public class UpdateCheckTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public HttpRequestMessage? LastRequest;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                LastRequest = request;
                return Task.FromResult(_respond(request));
            }
        }

        private static GitHubUpdateService Service(string body, HttpStatusCode code, Version current, out StubHandler handler)
        {
            handler = new StubHandler(_ => new HttpResponseMessage(code) { Content = new StringContent(body) });
            return new GitHubUpdateService("owner", "repo", current, new HttpClient(handler));
        }

        private const string ReleaseJson = """
            {
              "tag_name": "v0.7.4",
              "html_url": "https://github.com/owner/repo/releases/tag/v0.7.4",
              "body": "notes",
              "published_at": "2026-09-17T00:00:00Z",
              "assets": [
                { "name": "BODA.CMS-collector-0.7.4-x64.msi", "browser_download_url": "https://x/collector.msi", "size": 10 },
                { "name": "BODA.CMS-app-0.7.4-win-x64.zip", "browser_download_url": "https://x/app.zip", "size": 20 },
                { "name": "BODA.CMS-app-0.7.4-x64.msi", "browser_download_url": "https://x/app.msi", "size": 30,
                  "digest": "sha256:085d6423f52467130d2ef358b4cd57d0e1c6796ab04ea13e63b114ca09a229c1" }
              ]
            }
            """;

        [Theory]
        [InlineData("v0.7.3", "0.7.3")]
        [InlineData("V1.0.0", "1.0.0")]
        [InlineData("0.7.3", "0.7.3")]
        [InlineData("v0.7.3.1", "0.7.3.1")]
        public void TryParseTag_ValidTags(string input, string expected)
        {
            Assert.True(GitHubUpdateService.TryParseTag(input, out Version v));
            Assert.Equal(new Version(expected), v);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("release-2026-09")]
        [InlineData("v0.7.3-rc1")]
        [InlineData("v0.7.3+build1")]
        public void TryParseTag_InvalidTags(string? input)
        {
            Assert.False(GitHubUpdateService.TryParseTag(input, out _));
        }

        [Fact]
        public async Task CheckAsync_NewerRelease_PicksAppMsiAndFlagsUpdate()
        {
            using var svc = Service(ReleaseJson, HttpStatusCode.OK, new Version(0, 7, 3), out StubHandler handler);
            UpdateInfo? info = await svc.CheckAsync();

            Assert.NotNull(info);
            Assert.True(info!.IsUpdateAvailable);
            Assert.Equal("v0.7.4", info.LatestTagName);
            Assert.Equal("BODA.CMS-app-0.7.4-x64.msi", info.DownloadFileName);
            Assert.Equal("https://x/app.msi", info.DownloadUrl);
            Assert.Equal(30, info.DownloadSizeBytes);
            Assert.Equal("085d6423f52467130d2ef358b4cd57d0e1c6796ab04ea13e63b114ca09a229c1", info.DownloadSha256);
            Assert.Equal("https://github.com/owner/repo/releases/tag/v0.7.4", info.ReleaseUrl);
            Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", handler.LastRequest!.RequestUri!.ToString());
            Assert.Contains("application/vnd.github+json", handler.LastRequest.Headers.Accept.ToString());
        }

        [Theory]
        [InlineData("0.7.4")]
        [InlineData("0.8.0")]
        public async Task CheckAsync_SameOrNewerCurrent_NotAvailable(string current)
        {
            using var svc = Service(ReleaseJson, HttpStatusCode.OK, new Version(current), out _);
            UpdateInfo? info = await svc.CheckAsync();
            Assert.NotNull(info);
            Assert.False(info!.IsUpdateAvailable);
        }

        [Fact]
        public async Task CheckAsync_HttpError_ReturnsNull()
        {
            using var svc = Service("", HttpStatusCode.InternalServerError, new Version(0, 7, 3), out _);
            Assert.Null(await svc.CheckAsync());
        }

        [Fact]
        public async Task CheckAsync_InvalidJson_ReturnsNull()
        {
            using var svc = Service("{not json", HttpStatusCode.OK, new Version(0, 7, 3), out _);
            Assert.Null(await svc.CheckAsync());
        }

        [Fact]
        public async Task CheckAsync_UnparseableTag_ReturnsNull()
        {
            using var svc = Service("""{ "tag_name": "release-2026-09", "assets": [] }""", HttpStatusCode.OK, new Version(0, 7, 3), out _);
            Assert.Null(await svc.CheckAsync());
        }

        [Fact]
        public async Task CheckAsync_NoAppMsi_StillReportsRelease()
        {
            using var svc = Service("""{ "tag_name": "v0.9.0", "assets": [ { "name": "BODA.CMS-collector-0.9.0-x64.msi" } ] }""",
                HttpStatusCode.OK, new Version(0, 7, 3), out _);
            UpdateInfo? info = await svc.CheckAsync();
            Assert.NotNull(info);
            Assert.True(info!.IsUpdateAvailable);
            Assert.Equal(string.Empty, info.DownloadUrl);
            // html_url 이 없으면 releases/latest 로 폴백
            Assert.Equal("https://github.com/owner/repo/releases/latest", info.ReleaseUrl);
        }

        [Theory]
        [InlineData("sha256:085d6423f52467130d2ef358b4cd57d0e1c6796ab04ea13e63b114ca09a229c1", "085d6423f52467130d2ef358b4cd57d0e1c6796ab04ea13e63b114ca09a229c1")]
        [InlineData("SHA256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
        [InlineData("md5:abcdef", "")]
        [InlineData("sha256:tooshort", "")]
        [InlineData(null, "")]
        public void NormalizeSha256Digest(string? input, string expected)
        {
            Assert.Equal(expected, GitHubUpdateService.NormalizeSha256Digest(input));
        }
    }
}
