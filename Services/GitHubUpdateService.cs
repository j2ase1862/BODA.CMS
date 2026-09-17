using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BODA.CMS.Services
{
    /// <summary>
    /// GitHub Releases 조회 결과 + 현재 버전 비교. <see cref="GitHubUpdateService.CheckAsync"/> 반환 값.
    /// 비교가 불가능한 경우(통신·파싱 실패) 서비스는 null 을 돌려주며 호출 측은 "정보 없음"으로 다룬다.
    /// </summary>
    public sealed class UpdateInfo
    {
        public Version CurrentVersion { get; init; } = new(0, 0, 0);
        public Version LatestVersion { get; init; } = new(0, 0, 0);
        /// <summary>tag_name 원본(예: v0.7.3). UI 표시용.</summary>
        public string LatestTagName { get; init; } = string.Empty;
        public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
        /// <summary>모니터 앱 MSI(BODA.CMS-app-*.msi) 직접 다운로드 URL — 없으면 빈 문자열.</summary>
        public string DownloadUrl { get; init; } = string.Empty;
        public string DownloadFileName { get; init; } = string.Empty;
        public long DownloadSizeBytes { get; init; }
        /// <summary>MSI 자산 SHA-256(소문자 hex). GitHub digest 미제공이면 빈 문자열 — 2단계(자동 설치) 검증용.</summary>
        public string DownloadSha256 { get; init; } = string.Empty;
        /// <summary>릴리스 HTML 페이지 — 사용자가 브라우저로 내려받는 곳.</summary>
        public string ReleaseUrl { get; init; } = string.Empty;
        public string ReleaseNotes { get; init; } = string.Empty;
        public DateTime PublishedAt { get; init; }
    }

    /// <summary>
    /// 인앱 업데이트 알림 체커 (1단계). 배포 전용 public 리포의
    /// <c>https://api.github.com/repos/{owner}/{repo}/releases/latest</c> 를 조회해 실행 중인 어셈블리 버전과 비교한다.
    /// 다운로드·설치는 하지 않는다 — 사용자가 릴리스 페이지를 열어 직접 받는다(VMS 1단계와 동일 정책).
    ///
    /// 익명 호출(rate limit 60회/시/IP) — 앱 시작 1회 + 사용자 명시 확인이라 충분. 통신·파싱 실패는 예외 없이 null.
    /// </summary>
    public sealed class GitHubUpdateService : IDisposable
    {
        public const string DefaultOwner = "j2ase1862";
        public const string DefaultRepo = "CMS-Releases";
        private const string AppMsiPrefix = "BODA.CMS-app-";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _http;
        private readonly string _apiUrl;
        private bool _disposed;

        public Version CurrentVersion { get; }
        public string ReleasesUrl { get; }

        /// <summary>운영 ctor — 진입 어셈블리 버전(패키징 시 -p:Version)을 사용.</summary>
        public GitHubUpdateService(string owner = DefaultOwner, string repo = DefaultRepo)
            : this(owner, repo, ResolveCurrentVersion(), httpClient: null) { }

        /// <summary>테스트 친화 ctor — 버전·HttpClient 주입.</summary>
        public GitHubUpdateService(string owner, string repo, Version currentVersion, HttpClient? httpClient)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
            if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo required", nameof(repo));
            _apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            ReleasesUrl = $"https://github.com/{owner}/{repo}/releases/latest";
            CurrentVersion = currentVersion;
            _http = httpClient ?? BuildClient(currentVersion);
        }

        private static HttpClient BuildClient(Version version)
        {
            var client = new HttpClient { Timeout = RequestTimeout };
            // GitHub API 는 User-Agent 필수.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BODA.CMS", version.ToString()));
            return client;
        }

        public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[UpdateCheck] HTTP {(int)response.StatusCode} from {_apiUrl}");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var release = JsonSerializer.Deserialize<ReleaseDto>(json, JsonOptions);
                if (release is null) return null;
                if (!TryParseTag(release.TagName, out Version latest))
                {
                    Debug.WriteLine($"[UpdateCheck] tag_name 파싱 불가: '{release.TagName}'");
                    return null;
                }

                AssetDto? msi = FindAppMsi(release.Assets);
                return new UpdateInfo
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = latest,
                    LatestTagName = release.TagName ?? string.Empty,
                    DownloadUrl = msi?.BrowserDownloadUrl ?? string.Empty,
                    DownloadFileName = msi?.Name ?? string.Empty,
                    DownloadSizeBytes = msi?.Size ?? 0,
                    DownloadSha256 = NormalizeSha256Digest(msi?.Digest),
                    ReleaseUrl = string.IsNullOrEmpty(release.HtmlUrl) ? ReleasesUrl : release.HtmlUrl,
                    ReleaseNotes = release.Body ?? string.Empty,
                    PublishedAt = release.PublishedAt ?? DateTime.MinValue,
                };
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCheck] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }

        /// <summary>"v0.7.3" / "0.7.3" / "v0.7.3.1" → Version. pre-release(-rc1)·빌드 메타(+build)는 보수적으로 거부.</summary>
        internal static bool TryParseTag(string? tagName, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tagName)) return false;
            string trimmed = tagName.Trim().TrimStart('v', 'V');
            if (trimmed.Contains('-') || trimmed.Contains('+')) return false;
            return Version.TryParse(trimmed, out version!);
        }

        /// <summary>릴리스 자산 중 모니터 앱 MSI 선택(감시 서버 MSI·zip·setup 번들은 제외).</summary>
        internal static AssetDto? FindAppMsi(AssetDto[]? assets)
        {
            if (assets is null) return null;
            foreach (AssetDto a in assets)
            {
                if (!string.IsNullOrEmpty(a.Name)
                    && a.Name.StartsWith(AppMsiPrefix, StringComparison.OrdinalIgnoreCase)
                    && a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        /// <summary>GitHub asset digest("sha256:HEX") → 소문자 hex 64자. 형식이 다르면 빈 문자열(검증 생략 신호).</summary>
        internal static string NormalizeSha256Digest(string? digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return string.Empty;
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string hex = digest.Substring(prefix.Length).Trim().ToLowerInvariant();
            if (hex.Length != 64) return string.Empty;
            foreach (char c in hex)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return string.Empty;
            return hex;
        }

        /// <summary>현재 버전 오버라이드(검증용) — 예: BODA_CMS_VERSION=0.7.0 으로 실행하면 배지·다이얼로그 경로를 재현할 수 있다.</summary>
        public const string VersionOverrideEnv = "BODA_CMS_VERSION";

        internal static Version ResolveCurrentVersion()
        {
            string? overrideText = Environment.GetEnvironmentVariable(VersionOverrideEnv);
            if (!string.IsNullOrWhiteSpace(overrideText) && Version.TryParse(overrideText.Trim(), out Version? forced))
                return forced;

            Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // "0.7.3+abc123" 형태일 수 있음 — 접미사 제거.
                string clean = info.Split('+', '-')[0];
                if (Version.TryParse(clean, out Version? parsed)) return parsed;
            }
            return asm.GetName().Version ?? new Version(0, 0, 0);
        }

        internal sealed class ReleaseDto
        {
            [JsonPropertyName("tag_name")] public string? TagName { get; set; }
            [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
            [JsonPropertyName("body")] public string? Body { get; set; }
            [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
            [JsonPropertyName("assets")] public AssetDto[]? Assets { get; set; }
        }

        internal sealed class AssetDto
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
            [JsonPropertyName("size")] public long? Size { get; set; }
            [JsonPropertyName("digest")] public string? Digest { get; set; }
        }
    }
}
