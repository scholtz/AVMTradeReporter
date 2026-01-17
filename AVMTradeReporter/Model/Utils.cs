using Org.BouncyCastle.Crypto.Digests;
using System.Linq;
using System.Text;

namespace AVMTradeReporter.Model
{
    public static class Utils
    {
        public static byte[] DeltaValueStringToBytes(string inputAsciiOrHex)
        {
            if (inputAsciiOrHex.StartsWith("0x"))
            {
                // the input is in hex format, convert it to bytes
                var hex = inputAsciiOrHex[2..];
                return Convert.FromHexString(hex);
            }
            return Encoding.ASCII.GetBytes(inputAsciiOrHex);
        }
        public static string DeltaValueBytesToString(byte[] data)
        {
            return Encoding.ASCII.GetString(data);
        }
        public static ulong UInt256ToUlong(byte[] bytes)
        {
            ulong result = 0;
            foreach (byte b in bytes)
            {
                result = (result << 8) | b;
            }
            return result;
        }

        // Converts a base64-encoded 32-byte big-endian UInt256 delta value into a ulong by
        // extracting the least-significant 8 bytes. This matches the logic used in pool processors.
        // if input starts with "0x", treat it as hex
        public static ulong UInt256Base64DeltaToUlong(string inputBase64OrHex)
        {
            if (inputBase64OrHex.StartsWith("0x"))
            {
                // the input is in hex format, convert it to bytes
                var hex = inputBase64OrHex[2..];
                return UInt256Base64DeltaToUlong(Convert.FromHexString(hex));
            }
            return UInt256Base64DeltaToUlong(Convert.FromBase64String(inputBase64OrHex));
        }

        // Converts a base64-encoded 32-byte big-endian UInt256 delta value into a ulong by
        // extracting the least-significant 8 bytes. This matches the logic used in pool processors.
        public static ulong UInt256Base64DeltaToUlong(byte[] bytes)
        {
            // Take last 8 bytes (least significant) and reverse for little-endian BitConverter
            var last8 = bytes.Skip(bytes.Length - 8).Take(8).Reverse().ToArray();
            return BitConverter.ToUInt64(last8, 0);
        }

        public static byte[] ToARC4MethodSelector(string arc4MethodSignature)
        {
            var data = Encoding.ASCII.GetBytes(arc4MethodSignature);
            Sha512tDigest digest = new Sha512tDigest(256);
            digest.BlockUpdate(data, 0, data.Length);
            byte[] output = new byte[32];
            digest.DoFinal(output, 0);
            return output.Take(4).ToArray();
        }
    }
}
