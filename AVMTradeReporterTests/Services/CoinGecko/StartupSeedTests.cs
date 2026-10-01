using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// Indexer.Round is persisted when a block task starts, so after a crash it may sit above blocks whose trades were
    /// never stored. The watermark must start at the best known stored-through round (persisted or mirrored in Redis -
    /// both are written only after contiguous completion) and the indexer re-processes the gap.
    /// </summary>
    public class StartupSeedTests
    {
        [Test]
        public void NothingKnown_SeedsAtTheRoundBeforeTheIndexer_Unverified()
        {
            // indexer documents written before the field existed, no Redis: a guess that must not be mirrored
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, null, null), Is.EqualTo((999UL, false, false)));
        }

        [Test]
        public void StoredThroughBehindTheIndexer_RewindsToIt()
        {
            // crashed with blocks 997..999 in flight
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 996, null), Is.EqualTo((996UL, true, true)));
            // Elasticsearch was down for a long time: every parked block is re-processed, however many
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 500, null), Is.EqualTo((500UL, true, true)));
        }

        [Test]
        public void RedisMirrorAheadOfStoredThrough_IsTrusted_SoLatestBlockNeverGoesBackwards()
        {
            // blocks 997..998 completed (and were advertised) after the last increment persisted StoredThrough = 996
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 996, 998), Is.EqualTo((998UL, true, true)));
            // the mirror alone never rewinds: an operator who jumped Round forward cleared StoredThrough and the stale
            // 6-hour mirror must not undo that (an old indexer document crashing in its first run is the price)
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, null, 998), Is.EqualTo((999UL, false, false)));
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(60_000_000, null, 59_000_000), Is.EqualTo((59_999_999UL, false, false)));
            // another pod is ahead: advertise what Redis already advertises, do not rewind
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 999, 1200), Is.EqualTo((1200UL, false, true)));
        }

        [Test]
        public void StoredThroughUpToDate_NoRewind()
        {
            // graceful shutdown persisted the final watermark
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 999, 999), Is.EqualTo((999UL, false, true)));
            Assert.That(TradeReporterBackgroundService.ResolveStartupSeed(1000, 1200, null), Is.EqualTo((999UL, false, true)), "StoredThrough never ahead of the indexer");
        }
    }
}
