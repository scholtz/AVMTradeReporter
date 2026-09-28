using AVMTradeReporter.Services;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Regression coverage for the pool-details 24H-volume bug: VolumeUpdateBackgroundService only
    /// ever refreshed pools that had a *new* trade since the last tick
    /// (<c>TradeRepository.GetPoolsWithRecentTrades()</c>). A pool that stops trading is therefore
    /// never revisited again - its Volume1H/24H/7D are frozen forever at whatever they were the last
    /// time it traded, never decaying back toward zero as real time passes outside those windows.
    /// Fixed by adding a periodic full sweep across every cached pool address, independent of recent
    /// trade activity. These tests cover the pure scheduling decision
    /// (<see cref="VolumeUpdateBackgroundService.IsFullSweepDue"/> and
    /// <see cref="VolumeUpdateBackgroundService.DeterminePoolsToUpdate"/>), not the ES-backed volume
    /// recompute itself (see <c>PoolVolumeStaleResetTests</c> for that).
    /// </summary>
    public class VolumeUpdateBackgroundServiceTests
    {
        [Test]
        public void IsFullSweepDue_ReturnsFalse_BeforeIntervalElapsed()
        {
            var lastSweep = DateTimeOffset.UtcNow;
            var now = lastSweep.AddSeconds(30);

            var due = VolumeUpdateBackgroundService.IsFullSweepDue(now, lastSweep, TimeSpan.FromSeconds(900));

            Assert.That(due, Is.False);
        }

        [Test]
        public void IsFullSweepDue_ReturnsTrue_OnceIntervalElapsed()
        {
            var lastSweep = DateTimeOffset.UtcNow;
            var now = lastSweep.AddSeconds(900);

            var due = VolumeUpdateBackgroundService.IsFullSweepDue(now, lastSweep, TimeSpan.FromSeconds(900));

            Assert.That(due, Is.True);
        }

        [Test]
        public void DeterminePoolsToUpdate_WhenFullSweepDue_ReturnsEveryPool_IncludingQuietOnes()
        {
            var allPools = new[] { "quiet-pool", "active-pool" };
            var recentlyTraded = new[] { "active-pool" };

            var result = VolumeUpdateBackgroundService.DeterminePoolsToUpdate(
                fullSweepDue: true, poolsWithRecentTrades: recentlyTraded, allPoolAddresses: allPools);

            Assert.That(result, Is.EquivalentTo(new[] { "quiet-pool", "active-pool" }),
                "A due full sweep must include every cached pool, not just the ones with brand new " +
                "trades, so a pool that went quiet gets its stale volume corrected.");
        }

        [Test]
        public void DeterminePoolsToUpdate_WhenFullSweepNotDue_ReturnsOnlyRecentlyTradedPools()
        {
            var allPools = new[] { "quiet-pool", "active-pool" };
            var recentlyTraded = new[] { "active-pool" };

            var result = VolumeUpdateBackgroundService.DeterminePoolsToUpdate(
                fullSweepDue: false, poolsWithRecentTrades: recentlyTraded, allPoolAddresses: allPools);

            Assert.That(result, Is.EquivalentTo(new[] { "active-pool" }),
                "The fast incremental path between full sweeps must stay cheap and only touch pools " +
                "that actually traded.");
        }
    }
}
