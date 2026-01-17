using AVMTradeReporter.Model;

namespace AVMTradeReporterTests.Model
{
    public class UtilsUInt256ToUlongTests
    {
        [Test]
        public void UInt256Base64DeltaToUlong_ExtractsLeastSignificantUInt64()
        {
            // Arrange: construct a 32-byte big-endian array where the last 8 bytes represent 0x0102030405060708
            byte[] prefix = new byte[24]; // zeros
            byte[] last8 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }; // big-endian
            byte[] full = new byte[32];
            Buffer.BlockCopy(prefix, 0, full, 0, 24);
            Buffer.BlockCopy(last8, 0, full, 24, 8);
            string base64 = Convert.ToBase64String(full);

            // Act
            ulong value = Utils.UInt256Base64DeltaToUlong(base64);

            // Assert
            Assert.That(value, Is.EqualTo(0x0102030405060708UL));
        }

        [Test]
        public void UInt256ToUlong_ProcessesArbitraryLengthBigEndian()
        {
            // Arrange: same 8-byte sequence without base64 helper
            byte[] bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

            // Act
            ulong value = Utils.UInt256ToUlong(bytes);

            // Assert
            Assert.That(value, Is.EqualTo(0x0102030405060708UL));
        }
    }
}
