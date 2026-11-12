using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services
{
    /// <summary>The catch-up rules of the forward indexer (see CLAUDE.md, "Sync speed").</summary>
    [TestFixture]
    public class IndexerSyncPolicyTests
    {
        [Test]
        public void AtTheTip_IsNotCatchingUp()
        {
            // wait-for-block-after(round) answered: the tip is one block above the round about to be processed
            Assert.That(IndexerSyncPolicy.IsCatchingUp(knownTip: 1001, round: 1000, lagThresholdBlocks: 20), Is.False);
            Assert.That(IndexerSyncPolicy.IsCatchingUp(1020, 1000, 20), Is.False, "exactly the threshold is still the tip");
            Assert.That(IndexerSyncPolicy.IsCatchingUp(0, 1000, 20), Is.False, "tip unknown (no answer yet)");
            Assert.That(IndexerSyncPolicy.IsCatchingUp(900, 1000, 20), Is.False, "a node behind the indexer must not underflow into a huge lag");
        }

        [Test]
        public void BehindTheTip_IsCatchingUp_UnlessSwitchedOff()
        {
            // the 2026-11 mainnet restart: 108 826 blocks to re-process
            Assert.That(IndexerSyncPolicy.IsCatchingUp(66845524, 66736698, 20), Is.True);
            Assert.That(IndexerSyncPolicy.IsCatchingUp(1021, 1000, 20), Is.True);
            Assert.That(IndexerSyncPolicy.IsCatchingUp(66845524, 66736698, 0), Is.False, "0 disables the catch-up mode");
        }

        [Test]
        public void AlgodIsAskedForTheTip_OnlyWhenTheKnownOneIsReached()
        {
            Assert.That(IndexerSyncPolicy.MustWaitForBlock(knownTip: 0, round: 1000), Is.True, "first block of a run");
            Assert.That(IndexerSyncPolicy.MustWaitForBlock(1000, 1000), Is.True, "the block after the round is not known to exist");
            Assert.That(IndexerSyncPolicy.MustWaitForBlock(1001, 1000), Is.False, "the last answer already covers this round");
            Assert.That(IndexerSyncPolicy.MustWaitForBlock(66845524, 66736698), Is.False);

            // a whole backlog costs one request, not one per block
            ulong tip = 5000, round = 1000;
            var requests = 0;
            for (; round < tip; round++) if (IndexerSyncPolicy.MustWaitForBlock(tip, round)) requests++;
            Assert.That(requests, Is.Zero);
            Assert.That(IndexerSyncPolicy.MustWaitForBlock(tip, round), Is.True, "and then it asks again");
        }

        [Test]
        public void Concurrency_IsRaisedOnlyWhileCatchingUp()
        {
            Assert.That(IndexerSyncPolicy.ConcurrencyLimit(false, 3, 16), Is.EqualTo(3));
            Assert.That(IndexerSyncPolicy.ConcurrencyLimit(true, 3, 16), Is.EqualTo(16));
            Assert.That(IndexerSyncPolicy.ConcurrencyLimit(true, 8, 2), Is.EqualTo(8), "catching up never has fewer slots than the tip");
            Assert.That(IndexerSyncPolicy.ConcurrencyLimit(false, 0, 0), Is.EqualTo(1));
        }

        [Test]
        public void IndexerDocument_IsWrittenEveryBlockAtTheTip_AndThrottledWhileCatchingUp()
        {
            var second = TimeSpan.FromSeconds(1);
            Assert.That(IndexerSyncPolicy.ShouldPersistIndexer(false, TimeSpan.Zero, second), Is.True, "other tooling reads Round: every block at the tip");
            Assert.That(IndexerSyncPolicy.ShouldPersistIndexer(true, TimeSpan.FromMilliseconds(20), second), Is.False);
            Assert.That(IndexerSyncPolicy.ShouldPersistIndexer(true, second, second), Is.True);
        }

        [Test]
        public void Defaults_KeepTheCatchUpModeOn()
        {
            var config = new AppConfiguration().BlockProcessing;
            Assert.That(config.CatchUpLagBlocks, Is.GreaterThan(0));
            Assert.That(config.CatchUpMaxConcurrentTasks, Is.GreaterThan(config.MaxConcurrentTasks));
            Assert.That(config.CatchUpIndexerPersistSeconds, Is.GreaterThan(0));
        }
    }
}
