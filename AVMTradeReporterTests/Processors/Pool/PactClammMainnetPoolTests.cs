using System.Net;
using System.Text;
using Algorand.Algod;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
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

            public FakeAlgodHandler(ulong appId)
            {
                _appId = appId;
                _fixture = Fixture(appId);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
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
            var (processor, _, handler) = Create();
            var pool = await processor.LoadPoolAsync(Address, AppId);
            var swapTimestamp = DateTimeOffset.Now;
            handler.OnBoxListing = () =>
            {
                pool.A = 111;
                pool.B = 222;
                pool.Timestamp = swapTimestamp;
            };

            var refreshed = await processor.LoadPoolAsync(Address, AppId);

            Assert.That(refreshed, Is.SameAs(pool));
            Assert.That(refreshed.A, Is.EqualTo(111));
            Assert.That(refreshed.B, Is.EqualTo(222));
            Assert.That(refreshed.Timestamp, Is.EqualTo(swapTimestamp));
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
}
}
