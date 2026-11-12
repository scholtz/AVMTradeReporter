using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Our own algod is asked first whenever it has the block; the archival fallback (a public service with a rate limit
    /// and a quota) only for the blocks it no longer keeps. See CLAUDE.md, "Sync speed", rule 6.
    /// </summary>
    [TestFixture]
    public class AlgodBlockAvailabilityTests
    {
        private const int Own = 0;
        private const int Archival = 1;
        private const ulong Retry = AlgodBlockAvailability.RetryEveryRounds;

        [Test]
        public void ConfiguredOrder_AppliesUntilANodeDoesNotKeepABlock()
        {
            var availability = new AlgodBlockAvailability(3);
            Assert.That(availability.Order(1000), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void NodeThatDoesNotKeepABlock_IsNotAskedFirst_ButStaysTheLastResort()
        {
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);

            Assert.That(availability.Order(1001), Is.EqualTo(new[] { Archival, Own }), "the archival node first, our node still tried if that one fails");
            Assert.That(availability.Order(1000 + Retry), Is.EqualTo(new[] { Archival, Own }));
        }

        [Test]
        public void AfterTheRetryDistance_ExactlyOneBlockTaskProbesTheNode()
        {
            // 16 block tasks ask for the order of the rounds right after the skip distance at the same moment
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);

            var orders = new int[16][];
            Parallel.For(0, 16, i => orders[i] = availability.Order(1000 + Retry + 1 + (ulong)i).ToArray());

            Assert.That(orders.Count(o => o[0] == Own), Is.EqualTo(1), "one probe, not one failed request per concurrent block");
            Assert.That(orders.Count(o => o[0] == Archival), Is.EqualTo(15));
        }

        [Test]
        public void ProbeThatSucceeds_MakesTheNodeTheFirstChoiceAtOnce()
        {
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);
            Assert.That(availability.Order(1000 + Retry + 1)[0], Is.EqualTo(Own), "the probe");
            Assert.That(availability.Order(1000 + Retry + 2)[0], Is.EqualTo(Archival), "everybody else keeps skipping it while the probe is out");

            availability.MarkAvailable(Own);
            Assert.That(availability.Order(1000 + Retry + 3), Is.EqualTo(new[] { Own, Archival }));
        }

        [Test]
        public void ProbeThatFails_SkipsTheNodeForAnotherDistance()
        {
            var availability = new AlgodBlockAvailability(2);
            availability.MarkMissing(Own, 1000);
            var probeRound = 1000 + Retry + 1;
            Assert.That(availability.Order(probeRound)[0], Is.EqualTo(Own));
            availability.MarkMissing(Own, probeRound);

            Assert.That(availability.Order(probeRound + Retry)[0], Is.EqualTo(Archival));
            Assert.That(availability.Order(probeRound + Retry + 1)[0], Is.EqualTo(Own), "the next probe");
        }

        [Test]
        public void CatchUp_UsesTheArchivalNodeOnlyForTheBlocksOurNodeDoesNotKeep()
        {
            // our node keeps the last 1000 rounds; the backlog starts 5000 rounds behind the tip
            const ulong tip = 105_000, firstKept = tip - 1000;
            var availability = new AlgodBlockAvailability(2);
            int ownServed = 0, ownFailures = 0, archivalServed = 0;

            for (ulong round = 100_000; round <= tip; round++)
            {
                List<int>? notKept = null;
                foreach (var index in availability.Order(round))
                {
                    if (index == Archival || round >= firstKept)
                    {
                        availability.MarkAvailable(index);
                        if (notKept != null) foreach (var missing in notKept) availability.MarkMissing(missing, round);
                        if (index == Own) ownServed++; else archivalServed++;
                        break;
                    }
                    ownFailures++;
                    (notKept ??= new List<int>()).Add(index);
                }
            }

            Assert.That(ownFailures, Is.LessThanOrEqualTo(4000 / (int)Retry + 1), "one failed request per retry distance, not one per block");
            Assert.That(archivalServed, Is.InRange(4000, 4000 + (int)Retry), "the public archival node serves the old blocks only");
            Assert.That(ownServed, Is.GreaterThanOrEqualTo(1001 - (int)Retry), "our own node serves the blocks it keeps");
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
        public void OnlyTheNotKeptAnswer_CountsAsMissing()
        {
            // what our non-archival node answers for an old block
            Assert.That(TradeReporterBackgroundService.IsBlockNotKept(new Exception("failed to retrieve information from the ledger : ")), Is.True);
            Assert.That(TradeReporterBackgroundService.IsBlockNotKept(new Exception("wrapper", new Exception("Failed to retrieve information from the ledger"))), Is.True);

            // a hiccup says nothing about the other rounds: it must not move 200 blocks to the public fallback
            Assert.That(TradeReporterBackgroundService.IsBlockNotKept(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")), Is.False);
            Assert.That(TradeReporterBackgroundService.IsBlockNotKept(new HttpRequestException("Response status code does not indicate success: 502 (Bad Gateway).")), Is.False);
            Assert.That(TradeReporterBackgroundService.IsBlockNotKept(new Exception("Too Many Requests")), Is.False);
        }

        [Test]
        public void FallbackAlgods_AreNotConfiguredByDefault_AndEveryAlgodIsRateLimitedUnlessConfiguredOtherwise()
        {
            // the fallbacks used to default to a public MAINNET algod on every network (testnet and Voi included)
            var config = new AppConfiguration();
            Assert.That(config.Algod.Host, Is.EqualTo("https://algorand-algod-public.de-4.biatec.io"));
            Assert.That(config.Algod2.Host, Is.Empty);
            Assert.That(config.Algod3.Host, Is.Empty);
            // Limited unless the configuration says "own node" (0) next to the host: stage overrides only the host of the
            // primary with a public service, and a default of "no limit" would silently go with it. Below Nodely's 50
            // requests per second, with room for a second pod behind the same egress IP.
            Assert.That(config.Algod.MaxRequestsPerSecond, Is.InRange(1, 25));
            Assert.That(new AlgodConfiguration().MaxRequestsPerSecond, Is.InRange(1, 25));
        }
    }
}
