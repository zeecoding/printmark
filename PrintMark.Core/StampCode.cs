using System.Security.Cryptography;
using System.Text;
namespace PrintMark.Core;

public static class StampCodes
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026");

    public static (string HmacTag, string ShortCode) Create(StampMetadata m)
    {
        long ts = m.Timestamp.ToUnixTimeSeconds();
        string canonical = $"{m.KeyId}:{m.JobId}:{m.UserName}:{m.ComputerName}:{ts}";
        using var h = new HMACSHA256(Key);
        string tag = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(canonical)))[..16];
        return (tag, $"{m.KeyId}-{m.JobId:X}-{ts:X}-{tag}");
    }
}