using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BODA.CMS.Core.Licensing;
using BODA.CMS.Core.Telemetry;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>P5 라이선스 — 서명 검증·기한·등급 게이팅.</summary>
    public class LicensingTests : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly string _pubPem;
        private readonly string _dir = Directory.CreateTempSubdirectory("boda-lic").FullName;

        public LicensingTests() => _pubPem = _rsa.ExportSubjectPublicKeyInfoPem();

        public void Dispose()
        {
            _rsa.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string WriteLicense(string tier, string? expires, bool tamper = false)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                customer = "테스트고객",
                tier,
                issuedUtc = DateTime.UtcNow.ToString("O"),
                expiresUtc = expires,
            });
            byte[] sig = _rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (tamper) payload[10] ^= 0xFF;

            string path = Path.Combine(_dir, Guid.NewGuid() + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                payloadBase64 = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(sig),
            }));
            return path;
        }

        [Fact]
        public void 라이선스_파일이_없으면_평가판으로_전체_허용()
        {
            LicenseStatus s = LicenseVerifier.Load(Path.Combine(_dir, "없음.json"), _pubPem);

            Assert.Equal(LicenseMode.Trial, s.Mode);
            Assert.True(s.AllowsChannel(ProductTier.Basic));
            Assert.True(s.AllowsChannel(ProductTier.Pro));
        }

        [Fact]
        public void 유효한_Pro_라이선스는_전_등급_허용()
        {
            LicenseStatus s = LicenseVerifier.Load(WriteLicense("Pro", "2030-01-01"), _pubPem);

            Assert.Equal(LicenseMode.Licensed, s.Mode);
            Assert.Equal("테스트고객", s.Info!.Customer);
            Assert.True(s.AllowsChannel(ProductTier.Basic));
            Assert.True(s.AllowsChannel(ProductTier.Pro));
        }

        [Fact]
        public void Basic_라이선스는_Pro_채널을_막는다()
        {
            LicenseStatus s = LicenseVerifier.Load(WriteLicense("Basic", null), _pubPem);

            Assert.Equal(LicenseMode.Licensed, s.Mode);
            Assert.True(s.AllowsChannel(ProductTier.Basic));
            Assert.False(s.AllowsChannel(ProductTier.Pro));
        }

        [Fact]
        public void 변조된_라이선스는_Invalid로_Basic_강등()
        {
            LicenseStatus s = LicenseVerifier.Load(WriteLicense("Pro", null, tamper: true), _pubPem);

            Assert.Equal(LicenseMode.Invalid, s.Mode);
            Assert.True(s.AllowsChannel(ProductTier.Basic));
            Assert.False(s.AllowsChannel(ProductTier.Pro));
        }

        [Fact]
        public void 만료된_라이선스는_Expired로_Basic_강등()
        {
            LicenseStatus s = LicenseVerifier.Load(WriteLicense("Pro", "2025-01-01"), _pubPem,
                nowUtc: new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(LicenseMode.Expired, s.Mode);
            Assert.False(s.AllowsChannel(ProductTier.Pro));
        }

        [Fact]
        public void 다른_키로_서명된_라이선스는_거부된다()
        {
            string path = WriteLicense("Pro", null);
            using var otherKey = RSA.Create(2048);
            LicenseStatus s = LicenseVerifier.Load(path, otherKey.ExportSubjectPublicKeyInfoPem());

            Assert.Equal(LicenseMode.Invalid, s.Mode);
        }
    }
}
