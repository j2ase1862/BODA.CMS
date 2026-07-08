using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BODA.CMS.Core.Licensing;

// BODA.CMS 라이선스 발급 도구 (사내용).
//   init                                  : dev-keys/ 에 RSA 키쌍 생성 + Core에 붙일 공개키 출력
//   issue <customer> <tier> [expires]     : license.json 발급 (tier: Basic|Pro, expires: yyyy-MM-dd, 생략 시 무기한)
// ⚠️ dev-keys/private.pem 은 개발용이다 — 운영 발급 키는 저장소 밖 보안 저장소에서 관리하고
//    Core의 공개키 상수를 운영 공개키로 교체할 것.

string keyDir = Path.Combine(AppContext.BaseDirectory.Split(new[] { "bin" }, StringSplitOptions.None)[0], "dev-keys");
Directory.CreateDirectory(keyDir);
string privPath = Path.Combine(keyDir, "private.pem");
string pubPath = Path.Combine(keyDir, "public.pem");

switch (args.FirstOrDefault())
{
    case "init":
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(privPath, rsa.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
        File.WriteAllText(pubPath, rsa.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
        Console.WriteLine($"키쌍 생성: {privPath}");
        Console.WriteLine($"공개키(Core LicenseVerifier.DevPublicKeyPem 에 반영):\n{File.ReadAllText(pubPath)}");
        break;
    }
    case "issue":
    {
        if (args.Length < 3) { Console.WriteLine("usage: issue <customer> <Basic|Pro> [yyyy-MM-dd]"); return 1; }
        string customer = args[1];
        string tier = args[2];
        DateTime? expires = args.Length > 3 ? DateTime.Parse(args[3]).Date : null;

        var payload = new
        {
            customer,
            tier,
            issuedUtc = DateTime.UtcNow.ToString("O"),
            expiresUtc = expires?.ToString("yyyy-MM-dd"),
        };
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privPath));
        byte[] sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var file = new { payloadBase64 = Convert.ToBase64String(payloadBytes), signature = Convert.ToBase64String(sig) };
        string outPath = Path.Combine(Environment.CurrentDirectory, "license.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"발급 완료: {outPath}  ({customer} / {tier} / 만료 {expires?.ToString("yyyy-MM-dd") ?? "없음"})");

        // 자가 검증 (발급 즉시 Core 검증기로 확인)
        LicenseStatus check = LicenseVerifier.Load(outPath, File.ReadAllText(pubPath));
        Console.WriteLine($"검증: {check.Mode} — {check.Description}");
        break;
    }
    default:
        Console.WriteLine("usage: LicGen init | issue <customer> <Basic|Pro> [yyyy-MM-dd]");
        return 1;
}
return 0;
