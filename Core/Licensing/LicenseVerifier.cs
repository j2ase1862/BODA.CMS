using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Core.Licensing
{
    public enum LicenseMode
    {
        /// <summary>라이선스 파일 없음 — 평가판(전체 기능, 로그에 표기).</summary>
        Trial,
        /// <summary>유효한 서명·기한 — 등급대로 허용.</summary>
        Licensed,
        /// <summary>기한 만료 — Basic으로 강등.</summary>
        Expired,
        /// <summary>서명/형식 불량 — Basic으로 강등.</summary>
        Invalid,
    }

    public sealed record LicenseInfo(string Customer, ProductTier Tier, DateTime IssuedUtc, DateTime? ExpiresUtc);

    public sealed record LicenseStatus(LicenseMode Mode, LicenseInfo? Info, string Description)
    {
        /// <summary>이 라이선스로 해당 등급 채널을 라이브 사용할 수 있는가 (등급 = capability 자동 판정, ROADMAP §1).</summary>
        public bool AllowsChannel(ProductTier channelTier) => Mode switch
        {
            LicenseMode.Trial => true,                                  // 평가판: 전 등급 (배포 시 정책 재검토)
            LicenseMode.Licensed => channelTier <= Info!.Tier,
            _ => channelTier <= ProductTier.Basic,                      // 만료/불량: Basic 강등
        };
    }

    /// <summary>
    /// 서명된 라이선스 파일 검증 (ROADMAP §4 P5 — 구독/라이선스 모델).
    /// 파일 형식: { "payloadBase64": ..., "signature": ... } — 서명은 payload 원문 바이트의
    /// RSA-SHA256 (정규화 이슈 회피를 위해 JSON 재직렬화가 아닌 원문 바이트에 서명).
    /// </summary>
    public static class LicenseVerifier
    {
        /// <summary>개발 서명키의 공개키 (tools/LicGen init 산출).
        /// ⚠️ 운영 배포 시 운영 발급 키의 공개키로 교체 — 개인키는 저장소 밖 보안 저장소에.</summary>
        public const string DevPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAw5ZljZ3TisyLNRI/brdz
8GLi+NxXwlE1RDBIXHFivh7/URWhMjo+4zoAoRG7JLKQvqLpiry7mBtH11Mtk+Ea
COqtL48nQeCaMXQ77l04MFgm9XA5Fav/OyCu0Dbk7/nGoCJC6EywRCBuljns1him
YAUCSerV9cjv0Yaj6giegpP0QfgkH1PxYs7gx4aglLyOR8WgP4g67OAUeNb591/E
csMID08gpnBidhuU5b74xdp7WhhyldUTMFIpPaKDVk0RLa3ATJVqp40J7Tf3QXzJ
hR3H3AnrMVpQn0xdJ0OUmnK58zK5FO7m6dKqctFB5Abn+ST/rctq8N62JtOCeMRp
0QIDAQAB
-----END PUBLIC KEY-----";

        public static LicenseStatus Load(string path, string? publicKeyPem = null, DateTime? nowUtc = null)
        {
            if (!File.Exists(path))
                return new LicenseStatus(LicenseMode.Trial, null,
                    "평가판 — 라이선스 파일 없음. 전체 기능 사용 가능(상용 배포 시 라이선스 필요).");

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                byte[] payload = Convert.FromBase64String(doc.RootElement.GetProperty("payloadBase64").GetString()!);
                byte[] signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature").GetString()!);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKeyPem ?? DevPublicKeyPem);
                if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return new LicenseStatus(LicenseMode.Invalid, null, "라이선스 서명 불일치 — Basic으로 동작.");

                using JsonDocument p = JsonDocument.Parse(payload);
                var info = new LicenseInfo(
                    Customer: p.RootElement.GetProperty("customer").GetString()!,
                    Tier: Enum.Parse<ProductTier>(p.RootElement.GetProperty("tier").GetString()!, ignoreCase: true),
                    IssuedUtc: DateTime.Parse(p.RootElement.GetProperty("issuedUtc").GetString()!).ToUniversalTime(),
                    ExpiresUtc: p.RootElement.TryGetProperty("expiresUtc", out JsonElement e) && e.ValueKind == JsonValueKind.String
                        ? DateTime.Parse(e.GetString()!).Date
                        : null);

                DateTime now = nowUtc ?? DateTime.UtcNow;
                if (info.ExpiresUtc is DateTime exp && now.Date > exp)
                    return new LicenseStatus(LicenseMode.Expired, info,
                        $"라이선스 만료({exp:yyyy-MM-dd}) — Basic으로 동작. 고객: {info.Customer}");

                return new LicenseStatus(LicenseMode.Licensed, info,
                    $"정식 라이선스 — {info.Customer} / {info.Tier}" +
                    (info.ExpiresUtc is DateTime x ? $" / 만료 {x:yyyy-MM-dd}" : " / 무기한"));
            }
            catch (Exception ex)
            {
                return new LicenseStatus(LicenseMode.Invalid, null,
                    "라이선스 파일 불량 — Basic으로 동작: " + ex.Message);
            }
        }
    }
}
