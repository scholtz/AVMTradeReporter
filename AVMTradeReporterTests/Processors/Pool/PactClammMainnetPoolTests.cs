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
            private readonly JObject _fixture = JObject.Parse(File.ReadAllText("Data/pact-clamm-3762241535-algod.json"));
            public bool FailBoxes { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == $"/v2/applications/{AppId}") return Json(_fixture["application"]!.ToString());
                if (path == $"/v2/applications/{AppId}/boxes") return FailBoxes ? Error() : Json(_fixture["boxes"]!.ToString());
                if (path == $"/v2/applications/{AppId}/box")
                {
                    if (FailBoxes) return Error();
                    var name = WebUtility.UrlDecode(request.RequestUri.Query.Split("name=")[1].Split('&')[0]).Replace("b64:", "");
                    var value = _fixture["boxValues"]![name]?.ToString();
                    if (value == null) return Error(HttpStatusCode.NotFound);
                    return Json(new JObject { ["name"] = name, ["round"] = 66847380, ["value"] = value }.ToString());
                }
                return Error(HttpStatusCode.NotFound);
            }

            private static Task<HttpResponseMessage> Json(string body) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

            private static Task<HttpResponseMessage> Error(HttpStatusCode status = HttpStatusCode.InternalServerError) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"unavailable\"}", Encoding.UTF8, "application/json") });
        }

        private static (PactPoolProcessor Processor, MockPoolRepository Repository, FakeAlgodHandler Handler) Create()
        {
            var handler = new FakeAlgodHandler();
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
    }
}
