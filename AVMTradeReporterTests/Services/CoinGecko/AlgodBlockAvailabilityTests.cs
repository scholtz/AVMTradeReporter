using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// Our own algod is asked first whenever it has the block; the archival fallback (a public service with a rate limit
    /// and a quota) only for the blocks it no longer keeps. See CLAUDE.md, "Sync speed", rule 6.
    /// </summary>
    public class AlgodBlockAvailabilityTests
    {
        private const int Own = 0;
        private const int Archival = 1;

        [Test]
        public void ConfiguredOrder_AppliesUntilANodeLacksABlock()
        {
            var availability = new AlgodBlockAvailability(3);
            Assert.That(availability.Order(1000), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void NodeThatLackedABlock_IsNotAskedFirst_ButStaysTheLastResort()
        {
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);

            Assert.That(availability.Order(1001), Is.EqualTo(new[] { Archival, Own }), "the archival node first, our node still tried if that one fails");
            Assert.That(availability.Order(1000 + AlgodBlockAvailability.RetryEveryRounds), Is.EqualTo(new[] { Archival, Own }));
            Assert.That(availability.Order(1000 + AlgodBlockAvailability.RetryEveryRounds + 1), Is.EqualTo(new[] { Own, Archival }), "tried again after the retry distance");
        }

        [Test]
        public void CatchUp_UsesTheArchivalNodeOnlyForTheBlocksOurNodeDoesNotKeep()
        {
            // our node keeps the last 1000 rounds; the backlog starts 5000 rounds behind the tip
            const ulong tip = 105_000, firstKept = tip - 1000;
            var availability = new AlgodBlockAvailability(2);
            var ownRequests = 0;
            var ownFailures = 0;
            var archivalRequests = 0;

            for (ulong round = 100_000; round <= tip; round++)
            {
                List<int>? failed = null;
                foreach (var index in availability.Order(round))
                {
                    var has = index == Archival || round >= firstKept;
                    if (index == Own) ownRequests++; else archivalRequests++;
                    if (has)
                    {
                        if (failed != null) foreach (var missing in failed) availability.MarkMissing(missing, round);
                        break;
                    }
                    if (index == Own) ownFailures++;
                    (failed ??= new List<int>()).Add(index);
                }
            }

            Assert.That(ownFailures, Is.LessThanOrEqualTo(4000 / (int)AlgodBlockAvailability.RetryEveryRounds + 1), "one failed request per retry distance, not one per block");
            Assert.That(archivalRequests, Is.InRange(4000, 4000 + (int)AlgodBlockAvailability.RetryEveryRounds), "the public archival node serves the old blocks only");
            Assert.That(ownRequests - ownFailures, Is.GreaterThanOrEqualTo(1000 - (int)AlgodBlockAvailability.RetryEveryRounds), "our own node serves the blocks it keeps");
        }

        [Test]
        public void Reset_RestoresTheConfiguredOrder()
        {
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);
            availability.Reset();
            Assert.That(availability.Order(1001), Is.EqualTo(new[] { Own, Archival }));
        }

        [Test]
        public void FallbackAlgods_AreNotConfiguredByDefault()
        {
            // they used to default to a public MAINNET algod on every network (testnet and Voi included)
            var config = new AppConfiguration();
            Assert.That(config.Algod.Host, Is.EqualTo("https://algorand-algod-public.de-4.biatec.io"));
            Assert.That(config.Algod2.Host, Is.Empty);
            Assert.That(config.Algod3.Host, Is.Empty);
        }
    }
}
