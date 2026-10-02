using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services.CoinGecko;
using AVMTradeReporterTests.Conformance;
using Moq;
using System.Diagnostics;
using static AVMTradeReporterTests.Conformance.CoinGeckoTestKit;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Performance
{
    /// <summary>
    /// Offline performance budgets at MAINNET scale (3 400+ pools, ~100k events per 1000 blocks at the very top end). They run
    /// in CI on every pull request, so a change that makes startup warm-up, event building or the caches blow up (the same
    /// class of mistakes that broke the HA rollouts in 2026-08, see CLAUDE.md "HA deploys") fails before it is merged.
    /// Budgets are generous multiples of what a dev machine needs - they catch algorithmic regressions, not jitter.
    /// </summary>
    [Category("Performance")]
    public class CoinGeckoPerformanceTests
    {
        private static IPoolRepository ManyPools(int count, bool decimalsOnPool, out Mock<IPoolRepository> mock)
        {
            var pools = Enumerable.Range(1, count).Select(i => MakePool((ulong)(1_000_000 + i), 0, Usdc, decimalsOnPool)).ToDictionary(p => p.PoolAddress);
            mock = new Mock<IPoolRepository>();
            mock.Setup(p => p.GetAllPoolAddresses()).Returns(() => pools.Keys.ToList());
            mock.Setup(p => p.GetCachedPool(It.IsAny<string>())).Returns((string a) => pools.TryGetValue(a, out var pool) ? pool : null);
            return mock.Object;
        }

        [Test]
        public async Task Warmup_WithMainnetSizedPoolCache_IsFast()
        {
            var service = Service(new FakeSource(), ManyPools(5_000, decimalsOnPool: true, out _));
            var watch = Stopwatch.StartNew();
            await service.WarmUpAsync(default);
            watch.Stop();
            TestContext.Out.WriteLine($"warm-up of 5000 pools: {watch.ElapsedMilliseconds} ms");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2_000), "the warm-up blocks the port from opening (CLAUDE.md HA rule)");
            Assert.That((await service.GetPairAsync("1000001", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
        }

        [Test]
        public async Task Warmup_WithColdAssetCache_ResolvesDecimalsInParallel_NotOneRoundTripAfterAnother()
        {
            // 600 pools without decimals on the pool entry, 10 ms per asset lookup: sequential would take 12 s
            var assets = new Mock<IAssetRepository>();
            assets.Setup(a => a.GetAssetAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>())).Returns(async (ulong id, CancellationToken _) =>
            {
                await Task.Delay(10);
                return new BiatecAsset { Index = id, Params = new Algorand.Algod.Model.AssetParams { Name = "A", UnitName = "A", Decimals = 6, Total = 1 } };
            });
            assets.Setup(a => a.IsDeletedAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var service = Service(new FakeSource(), ManyPools(600, decimalsOnPool: false, out _), assets: assets.Object);

            var watch = Stopwatch.StartNew();
            await service.WarmUpAsync(default);
            watch.Stop();
            TestContext.Out.WriteLine($"cold warm-up of 600 pools x 2 assets @10 ms: {watch.ElapsedMilliseconds} ms");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(4_000), "decimals must be resolved in parallel");
            Assert.That((await service.GetPairAsync("1000001", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
        }

        [Test]
        public async Task BuildingTheBusiestRange_Of20000Events_IsFast_AndTheSecondRequestIsServedFromCache()
        {
            var pools = ManyPools(300, decimalsOnPool: true, out _);
            var source = new FakeSource();
            var rnd = new Random(42);
            var trades = new List<Trade>();
            for (var i = 0; i < 20_000; i++)
            {
                var appId = (ulong)(1_000_001 + rnd.Next(300));
                trades.Add(Swap("T" + i, 5_000_000UL + (ulong)(i / 20), (ulong)(i % 20 + 1), appId, i % 2 == 0));
            }
            source.Add(trades);
            var service = Service(source, pools, new FakeTracker { Latest = new IndexedBlock(6_000_000, 1_760_000_100) });

            var cold = Stopwatch.StartNew();
            var first = await service.GetEventsJsonAsync(5_000_000, 5_000_999, default);
            cold.Stop();
            var warm = Stopwatch.StartNew();
            var second = await service.GetEventsJsonAsync(5_000_000, 5_000_999, default);
            warm.Stop();

            TestContext.Out.WriteLine($"20000 events: cold {cold.ElapsedMilliseconds} ms ({first.Value!.Length / 1024} KiB), cached {warm.ElapsedMilliseconds} ms");
            Assert.That(first.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            using (var doc = System.Text.Json.JsonDocument.Parse(first.Value!))
            {
                // a benchmark that silently maps nothing proves nothing: every one of the 20 000 swaps must be in the answer
                Assert.That(doc.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(20_000));
                Assert.That(GeckoTerminalSchema.ValidateEvents(doc.RootElement, 5_000_000, 5_000_999), Is.Empty);
            }
            Assert.That(first.Value!.Length, Is.GreaterThan(5_000_000));
            Assert.That(cold.ElapsedMilliseconds, Is.LessThan(4_000));
            Assert.That(warm.ElapsedMilliseconds, Is.LessThan(100), "an immutable range is served from the cache");
            Assert.That(second.Value, Is.SameAs(first.Value));
        }

        [Test]
        public async Task TwoHundredConcurrentIdenticalRequests_CostOneStorageQuery()
        {
            var source = new FakeSource { QueryDelay = TimeSpan.FromMilliseconds(50) };
            source.Add(new[] { Swap("S", 5_000_001, 1, 1_000_001) });
            var service = Service(source, ManyPools(10, true, out _), new FakeTracker { Latest = new IndexedBlock(6_000_000, 1) });

            var watch = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => service.GetEventsJsonAsync(5_000_000, 5_000_999, default))));
            watch.Stop();

            Assert.That(results.All(r => r.Outcome == CoinGeckoOutcome.Ok));
            Assert.That(source.Queries, Is.LessThanOrEqualTo(4), "single flight + cache: not 400 queries (trades + liquidity per request)");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(3_000));
        }

        [Test]
        public async Task SteadyStatePolling_ForOneHour_CostsOneCheapQueryPerBlock()
        {
            // GeckoTerminal polls every ~2 s: latest-block, then events(previous+1 .. latest). 1 300 blocks = one hour of mainnet.
            var source = new FakeSource();
            source.Add(Enumerable.Range(1, 1300).Select(i => Swap("P" + i, 5_000_000UL + (ulong)i, 1, 1_000_001)).ToList());
            var tracker = new FakeTracker { Latest = new IndexedBlock(5_000_000, 1) };
            var service = Service(source, ManyPools(50, true, out _), tracker);

            var watch = Stopwatch.StartNew();
            for (ulong block = 5_000_001; block <= 5_001_300; block++)
            {
                tracker.Latest = new IndexedBlock(block, 1_760_000_000 + (long)(block - 5_000_000) * 3);
                Assert.That((await service.GetLatestBlockAsync(default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
                var events = await service.GetEventsJsonAsync(block, block, default);
                Assert.That(events.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
                Assert.That(System.Text.Encoding.UTF8.GetString(events.Value!), Does.Contain("\"eventType\":\"swap\""), $"block {block}");
            }
            watch.Stop();
            TestContext.Out.WriteLine($"1300 polling cycles: {watch.ElapsedMilliseconds} ms, {source.Queries} storage queries");
            Assert.That(source.Queries, Is.EqualTo(2 * 1300), "exactly two storage queries (swaps, liquidity) per polled block");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(5_000));
        }

        [Test]
        public void MappingThroughput_Of100000Swaps()
        {
            var pair = new PairInfo(1, DEXProtocol.Biatec, 0, Usdc, 6, 6, 0.003m);
            var watch = Stopwatch.StartNew();
            var mapped = 0;
            for (var i = 0; i < 100_000; i++)
            {
                if (CoinGeckoMapper.TryMapSwap(Swap("T" + i, 1, (ulong)i, 1, i % 2 == 0), pair, new EventPositionPair((ulong)i, 0)) != null) mapped++;
            }
            watch.Stop();
            TestContext.Out.WriteLine($"100000 swaps mapped in {watch.ElapsedMilliseconds} ms");
            Assert.That(mapped, Is.EqualTo(100_000));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(3_000));
        }
    }
}
