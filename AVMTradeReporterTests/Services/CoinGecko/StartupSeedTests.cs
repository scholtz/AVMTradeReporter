using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// Indexer.Round is persisted when a block task starts, so after a crash it may sit above blocks whose trades were
    /// never stored. The watermark must start at the last mirrored latest-block and the indexer must re-process the gap.
    /// </summary>
    public class StartupSeedTests
    {
        [Test]
        public void NoMirroredWatermark_SeedsAtTheRoundBeforeTheIndexer()
        {
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, null, 50), Is.EqualTo((999UL, false)));
        }

        [Test]
        public void MirroredWatermarkBehindTheIndexer_RewindsToIt()
        {
            // crashed with blocks 997..999 in flight: Redis still says 996
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 996, 50), Is.EqualTo((996UL, true)));
        }

        [Test]
        public void MirroredWatermarkEqualOrAhead_NoRewind()
        {
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 999, 50), Is.EqualTo((999UL, false)));
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 1200, 50), Is.EqualTo((999UL, false)), "another pod is ahead - this one cannot verify those blocks");
        }

        [Test]
        public void GapBeyondTheLimit_IsLeftAlone()
        {
            // a stale mirror from a deliberate reset / another indexer must not drag the indexer back thousands of blocks
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 900, 50), Is.EqualTo((999UL, false)));
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 949, 50), Is.EqualTo((949UL, true)), "exactly the limit still rewinds");
        }
    }
}
