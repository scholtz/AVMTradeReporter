using System.Security.Cryptography;
using System.Text;

namespace AVMTradeReporter.Extensions
{
    public static class BytesExtension
    {
        public static string ToSha256Hex(this byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
