using System.Net;
using System.Text;
using Algorand.Algod;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AVMTradeReporterTests.Processors.Pool
{
    /// <summary>
    /// Loading the mainnet Pact CLAMM ALGO/USDC pool 3762241535 offline, from the captured algod responses in
    /// Data/pact-clamm-3762241535-algod.json (application, box list and the tick word boxes).
    /// </summary>
    public class PactClammMainnetPoolTests
    {
        private const ulong AppId = 3762241535;
        private const string Address = "TEXF2ZPVYW55BBUKV35GCX5GC27J7FBINBYHWTKZ4RED3ZTP2SEM4EEBL4";

        /// <summary>
        /// Serves the captured algod responses; the box endpoints can be switched off to simulate an algod failure.
        /// </summary>
        private sealed class FakeAlgodHandler : HttpMessageHandler
        {
            private readonly ulong _appId;
            private readonly JObject _fixture;
            public bool FailBoxes { get; set; }
            /// <summary>Answers the tick word boxes without a value (a broken node response).</summary>
            public bool NullBoxValues { get; set; }
            /// <summary>Runs when the box listing is requested, i.e. while the processor is reading the chain.</summary>
            public Action? OnBoxListing { get; set; }
            /// <summary>Number of algod requests served.</summary>
            public int Requests;

            public FakeAlgodHandler(ulong appId)
            {
                _appId = appId;
                _fixture = Fixture(appId);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                var path = request.RequestUri!.AbsolutePath;
                if (path == $"/v2/applications/{_appId}") return Json(_fixture["application"]!.ToString());
                if (path == $"/v2/applications/{_appId}/boxes")
                {
                    OnBoxListing?.Invoke();
                    return FailBoxes ? Error() : Json(_fixture["boxes"]!.ToString());
                }
                if (path == $"/v2/applications/{_appId}/box")
                {
                    if (FailBoxes) return Error();
                    var name = WebUtility.UrlDecode(request.RequestUri.Query.Split("name=")[1].Split('&')[0]).Replace("b64:", "");
                    var value = _fixture["boxValues"]![name]?.ToString();
                    if (value == null) return Error(HttpStatusCode.NotFound);
                    if (NullBoxValues) return Json(new JObject { ["name"] = name, ["round"] = 66847380 }.ToString());
                    return Json(new JObject { ["name"] = name, ["round"] = 66847380, ["value"] = value }.ToString());
                }
                return Error(HttpStatusCode.NotFound);
            }

            private static Task<HttpResponseMessage> Json(string body) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

            private static Task<HttpResponseMessage> Error(HttpStatusCode status = HttpStatusCode.InternalServerError) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"unavailable\"}", Encoding.UTF8, "application/json") });
        }

        private static JObject Fixture(ulong appId) => JObject.Parse(File.ReadAllText($"Data/pact-clamm-{appId}-algod.json"));

        private static (PactPoolProcessor Processor, MockPoolRepository Repository, FakeAlgodHandler Handler) Create(ulong appId = AppId)
        {
            var handler = new FakeAlgodHandler(appId);
            var algod = new DefaultApi(new HttpClient(handler) { BaseAddress = new Uri("http://algod.test") });
            var repository = new MockPoolRepository();
            var processor = new PactPoolProcessor(algod, repository, new LoggerFactory().CreateLogger<PactPoolProcessor>(), new MockAssetRepository());
            return (processor, repository, handler);
        }

        [Test]
        public async Task LoadsLiquidityRangesAndPactTvl()
        {
            var (processor, repository, _) = Create();
            var before = DateTimeOffset.Now;

            var pool = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(pool.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));
            Assert.That(pool.LiquidityRanges, Has.Count.EqualTo(50));
            Assert.That(pool.CurrentTick, Is.EqualTo(1000401));
            // 169,922 ALGO + 3,656.49 USDC = 18,937.7 USD - Pact shows 18.93k
            Assert.That(pool.RealAmountA, Is.EqualTo(169_922m).Within(1m));
            Assert.That(pool.RealAmountB, Is.EqualTo(3_656.49m).Within(0.01m));
            Assert.That(pool.RealAmountA * pool.CurrentPrice!.Value + pool.RealAmountB, Is.EqualTo(18_937.7m).Within(1m));
            // the reserves are the sum of the ranges, and L is the liquidity of the contract's current tick
            Assert.That((decimal)pool.A!.Value, Is.EqualTo(pool.LiquidityRanges!.Sum(r => (decimal)r.A)).Within(pool.LiquidityRanges.Count));
            Assert.That((decimal)pool.B!.Value, Is.EqualTo(pool.LiquidityRanges.Sum(r => (decimal)r.B)).Within(pool.LiquidityRanges.Count));
            Assert.That(pool.L, Is.EqualTo(337_594_319_121UL));
            // stamped with the time the read started minus the safety margin
            Assert.That(pool.Timestamp, Is.GreaterThanOrEqualTo(before - PactPoolProcessor.ReadTimestampSafetyMargin));
            Assert.That(pool.Timestamp, Is.LessThanOrEqualTo(DateTimeOffset.Now - PactPoolProcessor.ReadTimestampSafetyMargin));
            Assert.That((await repository.GetPoolAsync(Address, CancellationToken.None))?.LiquidityRanges, Has.Count.EqualTo(50));
        }

        [Test]
        public async Task ReloadReplacesDriftedReservesAndNeverMovesTheTimestampBack()
        {
            var (processor, repository, _) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            // the production state before the fix: event tracked reserves that missed every mainnet liquidity event,
            // ranges dropped by a liquidity event, and a block timestamp newer than the read
            pool.A = 68_681_971_506;
            pool.B = 748_572_371;
            pool.LiquidityRanges = null;
            var eventTimestamp = DateTimeOffset.Now.AddSeconds(-5);
            pool.Timestamp = eventTimestamp;

            var reloaded = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(reloaded.LiquidityRanges, Has.Count.EqualTo(50));
            Assert.That(reloaded.RealAmountA, Is.EqualTo(169_922m).Within(1m));
            Assert.That(reloaded.RealAmountB, Is.EqualTo(3_656.49m).Within(0.01m));
            Assert.That(reloaded.Timestamp, Is.EqualTo(eventTimestamp));
            Assert.That(await repository.GetPoolAsync(Address, CancellationToken.None), Is.SameAs(reloaded));
        }

        [Test]
        public async Task BoxReadFailureRepricesTheStoredRanges()
        {
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            var ranges = pool.LiquidityRanges!;
            var expectedA = pool.A;
            var expectedB = pool.B;
            pool.A = 1;
            pool.B = 1;
            handler.FailBoxes = true;

            var reloaded = await processor.LoadPoolAsync(Address, AppId);

            // still valid (no liquidity event dropped them): kept and repriced at the current price
            Assert.That(TickMath.SameRanges(reloaded.LiquidityRanges, ranges), Is.True);
            Assert.That(reloaded.A, Is.EqualTo(expectedA));
            Assert.That(reloaded.B, Is.EqualTo(expectedB));
            Assert.That(reloaded.L, Is.EqualTo(337_594_319_121UL));
        }

        [Test]
        public async Task BoxReadFailureWithoutRangesKeepsTheTrackedReserves()
        {
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            pool.LiquidityRanges = null; // dropped by a liquidity event
            pool.A = 170_000_000_000;
            pool.B = 3_600_000_000;
            handler.FailBoxes = true;

            var reloaded = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(reloaded.LiquidityRanges, Is.Null);
            Assert.That(reloaded.A, Is.EqualTo(170_000_000_000));
            Assert.That(reloaded.B, Is.EqualTo(3_600_000_000));
        }
    
        /// <summary>
        /// Pools whose price of A in B is above 1 base unit: tick origin d = 0, ticks below 1e6 (words below 1e6) and the
        /// contract current_price is sqrt(price of B in A). Before, those words were dropped and current_price was read as
        /// A in B, so the backend reported e.g. 1,039 USD for USDC/HAY 3764935215 (Pact: 22,495 USD) and priced HAY at the
        /// inverse of its real price. The fixtures carry the Pact API TVL read at the same time as the chain state.
        /// </summary>
        [TestCase(3764935215UL, "XL5VFN47OC67KVFM2PYKKWMSCSQMGQCKHYB7EMU5ULQKQ6LZOALJ2Y3JCY", 10_898.60, 1_376_750.78, 118.2469, 129)]
        [TestCase(3764749314UL, "2M75ASPFJ5YWJOYU676IOL3NX23H6R333RWX4WONRALEWTJA5FQWDOH4GQ", 85.5636, 474_046.18, 2764.03, 836)]
        public async Task PoolsAbovePriceOneMatchPactTvl(ulong appId, string address, double expectedA, double expectedB, double expectedPrice, int expectedTicks)
        {
            var (processor, _, _) = Create(appId);
            var reference = (JObject)Fixture(appId)["pact"]!;

            var pool = await processor.LoadPoolAsync(address, appId);

            Assert.That(pool.TickOrigin, Is.EqualTo(0));
            Assert.That(pool.LiquidityRanges!.Sum(r => (int)(r.TickHigh - r.TickLow + 1)), Is.EqualTo(expectedTicks));
            Assert.That((double)pool.RealAmountA, Is.EqualTo(expectedA).Within(0.01));
            Assert.That((double)pool.RealAmountB, Is.EqualTo(expectedB).Within(0.01));
            // 1 USDC buys ~118 HAY / ~2764 POW (the Pact API prices agree), not their inverse
            Assert.That((double)pool.CurrentPrice!.Value, Is.EqualTo(expectedPrice).Within(expectedPrice * 0.0001));
            var priceA = decimal.Parse(reference["primary"]!["price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var priceB = decimal.Parse(reference["secondary"]!["price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That((double)(priceA / priceB), Is.EqualTo(expectedPrice).Within(expectedPrice * 0.005));
            var tvl = pool.RealAmountA * priceA + pool.RealAmountB * priceB;
            var pactTvl = decimal.Parse(reference["tvlUsd"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(tvl, Is.EqualTo(pactTvl).Within(pactTvl * 0.0001m));
            // the price lies in the range of the contract's current tick, which carries the active liquidity
            var active = pool.LiquidityRanges!.Single(r => r.TickLow <= pool.CurrentTick && pool.CurrentTick <= r.TickHigh);
            Assert.That(pool.CurrentPrice, Is.InRange(active.PMin, active.PMax));
            Assert.That(pool.L, Is.EqualTo(active.Liquidity));
        }

        [Test]
        public async Task EventDuringTheReadKeepsTheEventState()
        {
            // a swap is applied to the cached pool while a refresh is reading the chain: the refresh's state may predate the
            // swap and must not overwrite it
            // a swap with the same block timestamp as the pool (a second swap of the same block) is detected too
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            var timestamp = pool.Timestamp;
            pool.LiquidityRanges = null; // e.g. dropped by a mint whose reload this is - a swap must not starve the reload
            pool.LPFee = 0.5m; // stale metadata is still refreshed from the read
            const ulong swapSqrtPrice = 5537708898821207296UL;
            handler.OnBoxListing = () => PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetIdOut = 31566704, PoolSqrtPriceX64 = swapSqrtPrice });
            var expectedPrice = TickMath.SqrtPriceX64ToPrice(swapSqrtPrice, 6, 6);

            var refreshed = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(refreshed, Is.SameAs(pool));
            Assert.That(refreshed.CurrentPrice, Is.EqualTo(expectedPrice), "the read state overwrote the swap applied meanwhile");
            Assert.That(refreshed.Timestamp, Is.EqualTo(timestamp)); // the price state is the swap's: no read-time stamp
            Assert.That(refreshed.LPFee, Is.EqualTo(0.003m));
            // a swap never changes the per-tick liquidity: the fresh ranges are taken and priced at the swap's price
            Assert.That(refreshed.LiquidityRanges, Has.Count.EqualTo(50));
            var expected = TickMath.ApplyPrice(TickMath.CloneRanges(refreshed.LiquidityRanges!), swapSqrtPrice, 60, TickMath.TickBase, 6, 6);
            Assert.That(refreshed.A, Is.EqualTo(expected.A));
            Assert.That(refreshed.B, Is.EqualTo(expected.B));
            // without an event during the read the chain state is taken
            handler.OnBoxListing = null;
            var again = await processor.LoadPoolAsync(Address, AppId);
            Assert.That(again.CurrentPrice, Is.EqualTo(TickMath.SqrtPriceX64ToPrice(5531894802404564755UL, 6, 6)));
        }

        [Test]
        public async Task MissingBoxValueIsAFailedReadNotEmptyLiquidity()
        {
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            pool.LiquidityRanges = null; // dropped by a liquidity event
            pool.A = 170_000_000_000;
            pool.B = 3_600_000_000;
            handler.NullBoxValues = true;

            var reloaded = await processor.LoadPoolAsync(Address, AppId);

            // not an empty range list with zero reserves: the event tracked reserves stay
            Assert.That(reloaded.LiquidityRanges, Is.Null);
            Assert.That(reloaded.A, Is.EqualTo(170_000_000_000));
            Assert.That(reloaded.B, Is.EqualTo(3_600_000_000));
        }

        [Test]
        public async Task LiquidityEventDuringTheReadKeepsTheEventState()
        {
            // the read ranges may predate the mint: keep the event state, the mint's own reload reads the boxes again
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            handler.OnBoxListing = () => PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                AssetIdA = 0,
                AssetIdB = 31566704,
                AssetAmountA = 1_000_000,
                AssetAmountB = 100_000,
            });
            var aAfterMint = pool.A!.Value + 1_000_000;

            var refreshed = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(refreshed.LiquidityRanges, Is.Null);
            Assert.That(refreshed.A, Is.EqualTo(aAfterMint));
        }

        /// <summary>
        /// The real in-memory pool repository (no Elasticsearch / Redis) whose Pact pool processor reads the fake algod.
        /// </summary>
        private static (PoolRepository Repository, FakeAlgodHandler Handler) CreateRepository()
        {
            var handler = new FakeAlgodHandler(AppId);
            var algod = new DefaultApi(new HttpClient(handler) { BaseAddress = new Uri("http://algod.test") });
            var appConfig = new OptionsWrapper<AppConfiguration>(new AppConfiguration());
            PoolRepository? repository = null;
            var services = new ServiceCollection();
            services.AddSingleton(_ => new PactPoolProcessor(algod, repository!, new LoggerFactory().CreateLogger<PactPoolProcessor>(), new MockAssetRepository()));
            var aggregated = new AggregatedPoolRepository(null!, new LoggerFactory().CreateLogger<AggregatedPoolRepository>(), null!, appConfig, null!, null, null);
            repository = new PoolRepository(null!, new LoggerFactory().CreateLogger<PoolRepository>(), null!, aggregated, appConfig, services.BuildServiceProvider());
            return (repository, handler);
        }

        /// <summary>The production state before the fix: event tracked reserves, no ranges, no tick origin.</summary>
        private static AVMTradeReporter.Models.Data.Pool LegacyPool() => new()
        {
            PoolAddress = Address,
            PoolAppId = AppId,
            Protocol = DEXProtocol.Pact,
            AMMType = AMMType.TickBasedCLAMM,
            ApprovalProgramHash = "ec1899eb1b0ee0c6a4263e38dfab158f2cfa4c0942c0c92c0fb0001965760053",
            AssetIdA = 0,
            AssetIdB = 31566704,
            AssetADecimals = 6,
            AssetBDecimals = 6,
            A = 68_681_971_506,
            B = 748_572_371,
            TickSpacing = 60,
            CurrentTick = 1000401,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
        };

        [Test]
        public async Task RepositoryReloadsTheRangesOnALiquidityEvent()
        {
            var (repository, _) = CreateRepository();
            await repository.StorePoolAsync(LegacyPool(), false, CancellationToken.None);
            var eventTime = DateTimeOffset.UtcNow.AddSeconds(-5);

            await repository.UpdatePoolFromLiquidity(new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                PoolAddress = Address,
                PoolAppId = AppId,
                Protocol = DEXProtocol.Pact,
                AssetIdA = 0,
                AssetIdB = 31566704,
                AssetAmountA = 37_627_392,
                AssetAmountB = 4_000_000,
                TxState = TxState.Confirmed,
                Timestamp = eventTime,
            }, CancellationToken.None);

            var pool = await repository.GetPoolAsync(Address, CancellationToken.None);
            Assert.That(pool!.LiquidityRanges, Has.Count.EqualTo(50));
            Assert.That(pool.RealAmountA, Is.EqualTo(169_922m).Within(1m));
            Assert.That(pool.RealAmountB, Is.EqualTo(3_656.49m).Within(0.01m));
            Assert.That(pool.TickOrigin, Is.EqualTo(TickMath.TickBase));
            // the block time of the event is newer than the read stamp: it is kept, later events still apply
            Assert.That(pool.Timestamp, Is.EqualTo(eventTime));
        }

        [Test]
        public async Task RepositoryReadsATickPoolWithoutRangesOnItsNextSwap()
        {
            // the swap carries current_tick (as most swaps do), which lets ApplyTrade infer the tick origin - the pool still
            // has to be read from chain, it has no ranges
            var (repository, handler) = CreateRepository();
            await repository.StorePoolAsync(LegacyPool(), false, CancellationToken.None);

            await repository.UpdatePoolFromTrade(new Trade
            {
                PoolAddress = Address,
                PoolAppId = AppId,
                Protocol = DEXProtocol.Pact,
                AssetIdIn = 0,
                AssetIdOut = 31566704,
                AssetAmountIn = 13_600_000,
                AssetAmountOut = 1_219_404,
                PoolSqrtPriceX64 = 5531894802404564755UL,
                PoolTick = 1000401,
                TradeState = TxState.Confirmed,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5),
            }, CancellationToken.None);

            var pool = await repository.GetPoolAsync(Address, CancellationToken.None);
            Assert.That(handler.Requests, Is.GreaterThan(0), "the pool was not read from chain");
            Assert.That(pool!.LiquidityRanges, Has.Count.EqualTo(50));
            Assert.That(pool.RealAmountA, Is.EqualTo(169_922m).Within(1m));
            Assert.That(pool.RealAmountB, Is.EqualTo(3_656.49m).Within(0.01m));
        }

        [Test]
        public async Task RepositoryRepricesASwapFromTheRangesWithoutReadingTheChain()
        {
            var (repository, handler) = CreateRepository();
            var loader = new PactPoolProcessor(
                new DefaultApi(new HttpClient(new FakeAlgodHandler(AppId)) { BaseAddress = new Uri("http://algod.test") }),
                new MockPoolRepository(), new LoggerFactory().CreateLogger<PactPoolProcessor>(), new MockAssetRepository());
            var pool = await loader.LoadPoolAsync(Address, AppId);
            pool.ApprovalProgramHash = "ec1899eb1b0ee0c6a4263e38dfab158f2cfa4c0942c0c92c0fb0001965760053";
            pool.Timestamp = DateTimeOffset.UtcNow.AddHours(-1);
            await repository.StorePoolAsync(pool, false, CancellationToken.None);
            const ulong before = 5537708898821207296UL; // an earlier price of the pool

            await repository.UpdatePoolFromTrade(new Trade
            {
                PoolAddress = Address,
                PoolAppId = AppId,
                Protocol = DEXProtocol.Pact,
                AssetIdIn = 0,
                AssetIdOut = 31566704,
                PoolSqrtPriceX64 = before,
                TradeState = TxState.Confirmed,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5),
            }, CancellationToken.None);

            var stored = await repository.GetPoolAsync(Address, CancellationToken.None);
            Assert.That(handler.Requests, Is.EqualTo(0), "a swap on a pool with ranges must not read the chain");
            var expected = TickMath.ApplyPrice(TickMath.CloneRanges(stored!.LiquidityRanges!), before, 60, TickMath.TickBase, 6, 6);
            Assert.That(stored.A, Is.EqualTo(expected.A));
            Assert.That(stored.B, Is.EqualTo(expected.B));
            Assert.That(stored.CurrentPrice, Is.EqualTo(TickMath.SqrtPriceX64ToPrice(before, 6, 6)));
        }
}
}
