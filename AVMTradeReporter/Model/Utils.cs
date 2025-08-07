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
    }
}
