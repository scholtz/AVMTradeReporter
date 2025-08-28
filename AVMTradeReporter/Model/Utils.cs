using Org.BouncyCastle.Crypto.Digests;
using System.Text;

namespace AVMTradeReporter.Model
{
    public static class Utils
    {
        public static byte[] DeltaValueStringToBytes(string data)
        {
            return Encoding.ASCII.GetBytes(data);
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
