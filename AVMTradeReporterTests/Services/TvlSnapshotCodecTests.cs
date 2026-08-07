using AVMTradeReporter.Services;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Tests for the hourly TVL snapshot value encoding (intra-hour OHLC + legacy single-number format).
    /// </summary>
    [TestFixture]
    public class TvlSnapshotCodecTests
    {
        [Test]
        public void Merge_FromNothing_CreatesFlatCandle()
        {
            var encoded = TvlSnapshotCodec.Merge(null, 100m);

            Assert.That(TvlSnapshotCodec.TryParse(encoded, out var o, out var h, out var l, out var c), Is.True);
            Assert.That(o, Is.EqualTo(100m));
            Assert.That(h, Is.EqualTo(100m));
            Assert.That(l, Is.EqualTo(100m));
            Assert.That(c, Is.EqualTo(100m));
        }

        [Test]
        public void Merge_TracksHighLowAndClose_KeepsOpen()
        {
            var encoded = TvlSnapshotCodec.Merge(null, 100m);
            encoded = TvlSnapshotCodec.Merge(encoded, 150m);
            encoded = TvlSnapshotCodec.Merge(encoded, 80m);
            encoded = TvlSnapshotCodec.Merge(encoded, 120m);

            Assert.That(TvlSnapshotCodec.TryParse(encoded, out var o, out var h, out var l, out var c), Is.True);
            Assert.That(o, Is.EqualTo(100m));
            Assert.That(h, Is.EqualTo(150m));
            Assert.That(l, Is.EqualTo(80m));
            Assert.That(c, Is.EqualTo(120m));
        }

        [Test]
        public void TryParse_LegacySingleNumber_DecodesAsFlatCandle()
        {
            Assert.That(TvlSnapshotCodec.TryParse("1234.56", out var o, out var h, out var l, out var c), Is.True);
            Assert.That(o, Is.EqualTo(1234.56m));
            Assert.That(h, Is.EqualTo(1234.56m));
            Assert.That(l, Is.EqualTo(1234.56m));
            Assert.That(c, Is.EqualTo(1234.56m));
        }

        [Test]
        public void Merge_LegacySingleNumber_UpgradesToOhlc()
        {
            var encoded = TvlSnapshotCodec.Merge("1000", 900m);

            Assert.That(TvlSnapshotCodec.TryParse(encoded, out var o, out var h, out var l, out var c), Is.True);
            Assert.That(o, Is.EqualTo(1000m));
            Assert.That(h, Is.EqualTo(1000m));
            Assert.That(l, Is.EqualTo(900m));
            Assert.That(c, Is.EqualTo(900m));
        }

        [Test]
        public void TryParse_Garbage_ReturnsFalse()
        {
            Assert.That(TvlSnapshotCodec.TryParse(null, out _, out _, out _, out _), Is.False);
            Assert.That(TvlSnapshotCodec.TryParse("", out _, out _, out _, out _), Is.False);
            Assert.That(TvlSnapshotCodec.TryParse("a;b;c;d", out _, out _, out _, out _), Is.False);
            Assert.That(TvlSnapshotCodec.TryParse("1;2", out _, out _, out _, out _), Is.False);
        }
    }
}
