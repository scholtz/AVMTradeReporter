using AVMTradeReporter.Hubs;

namespace AVMTradeReporterTests.Hubs;

public class BiatecScanHubOhlcTests
{
    [Test]
    public void OhlcGroupName_CanonicalizesPairOrder()
    {
        Assert.That(BiatecScanHub.OhlcGroupName(2UL, 1UL), Is.EqualTo("ohlc-1-2"));
        Assert.That(BiatecScanHub.OhlcGroupName(1UL, 2UL), Is.EqualTo("ohlc-1-2"));
        // USD self-pair (charting the USD reference asset itself)
        Assert.That(BiatecScanHub.OhlcGroupName(31566704UL, 31566704UL), Is.EqualTo("ohlc-31566704-31566704"));
        // USD series where base asset id > USD reference id maps to the same group
        // regardless of orientation the client subscribes with.
        Assert.That(BiatecScanHub.OhlcGroupName(99999999UL, 31566704UL), Is.EqualTo("ohlc-31566704-99999999"));
        Assert.That(BiatecScanHub.OhlcGroupName(31566704UL, 99999999UL), Is.EqualTo("ohlc-31566704-99999999"));
    }
}
