using System.Security.Cryptography;
using System.Text;

namespace PrintMark.Core;

/// <summary>
/// Signing + QR payload encryption. The master secret comes from the PRINTMARK_MASTER_KEY
/// environment variable (set it on the print server / client service account); the fallback
/// value is for the lab only. Separate sub-keys are derived for HMAC and AES so one key is
/// never used for two purposes.
/// </summary>
public static class StampCodes
{
    private static readonly byte[] Master = Encoding.UTF8.GetBytes(
        Environment.GetEnvironmentVariable("PRINTMARK_MASTER_KEY") ?? "LAB_PRINTMARK_SIGNING_SECRET_2026");

    private static readonly byte[] HmacKey = Derive("hmac");
    private static readonly byte[] AesKey = Derive("aes");

    private static byte[] Derive(string purpose) =>
        HMACSHA256.HashData(Master, Encoding.UTF8.GetBytes(purpose));

    /// <summary>16-hex-char HMAC tag over the canonical payload (used by the watermark and by server-side verification).</summary>
    public static string ComputeTag(string rawPayload) =>
        Convert.ToHexString(HMACSHA256.HashData(HmacKey, Encoding.UTF8.GetBytes(rawPayload)))[..16];

    public static (string HmacTag, string ShortCode) Create(StampMetadata m)
    {
        long ts = m.Timestamp.ToUnixTimeSeconds();
        string canonical = $"{m.KeyId}:{m.JobId}:{m.UserName}:{m.ComputerName}:{ts}";
        string tag = ComputeTag(canonical);
        return (tag, $"{m.KeyId}-{m.JobId:X}-{ts:X}-{tag}");
    }

    /// <summary>AES-256-GCM (authenticated). Output = base64(nonce[12] | tag[16] | ciphertext).</summary>
    public static string EncryptPayload(string payload)
    {
        byte[] plain = Encoding.UTF8.GetBytes(payload);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using var gcm = new AesGcm(AesKey, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string? DecryptPayload(string base64)
    {
        try
        {
            byte[] all = Convert.FromBase64String(base64);
            byte[] nonce = all[..12], tag = all[12..28], cipher = all[28..];
            byte[] plain = new byte[cipher.Length];
            using var gcm = new AesGcm(AesKey, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }
}
