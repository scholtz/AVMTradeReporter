using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// Indexer.Round is persisted when a block task starts, so after a crash it may sit above blocks whose trades were
    /// never stored. The watermark must start at the persisted stored-through round and the indexer re-processes the gap.
    /// </summary>
    public class StartupSeedTests
    {
        [Test]
        public void NoStoredThrough_SeedsAtTheRoundBeforeTheIndexer()
        {
            // indexer documents written before the field existed
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, null), Is.EqualTo((999UL, false)));
        }

        [Test]
        public void StoredThroughBehindTheIndexer_RewindsToIt()
        {
            // crashed with blocks 997..999 in flight
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 996), Is.EqualTo((996UL, true)));
            // Elasticsearch was down for a long time: every parked block is re-processed, however many
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 500), Is.EqualTo((500UL, true)));
        }

        [Test]
        public void StoredThroughUpToDate_NoRewind()
        {
            // graceful shutdown persisted the final watermark
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 999), Is.EqualTo((999UL, false)));
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 1200), Is.EqualTo((999UL, false)), "never ahead of the indexer");
        }
    }
}
