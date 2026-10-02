using AVMTradeReporter.Services.CoinGecko;
using System.Text.Json;
using static AVMTradeReporterTests.Conformance.CoinGeckoTestKit;

namespace AVMTradeReporterTests.Conformance
{
    /// <summary>
    /// Offline conformance: (1) the validator itself catches every kind of violation GeckoTerminal would halt on, and
    /// (2) everything the service produces - from the real mapper, serializer and ordering code - passes it.
    /// Runs in CI on every pull request, no network needed.
    /// </summary>
    public class GeckoTerminalSchemaTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private const string GoodSwap = """
            {"block":{"blockNumber":100,"blockTimestamp":1760000000},"eventType":"swap","txnId":"T","txnIndex":1,"eventIndex":0,"maker":"M","pairId":"1",
             "asset0In":"10","asset1Out":"1.5","priceNative":"0.15","reserves":{"asset0":"5","asset1":"7"},"metadata":{"fees0In":"0.03"}}
            """;

        // ------------------------------------------------------------------------------------------ the validator itself

        [Test]
        public void GoodSwap_IsConformant()
        {
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{GoodSwap}]}}"), 100, 100), Is.Empty);
        }

        [TestCase("\"blockTimestamp\":1760000000", "\"blockTimestamp\":1760000000000", "milliseconds")]
        [TestCase("\"eventType\":\"swap\"", "\"eventType\":\"trade\"", "eventType")]
        [TestCase("\"asset1Out\":\"1.5\"", "\"asset1Out\":\"1.5\",\"asset0Out\":\"1\"", "exactly")]
        [TestCase("\"asset0In\":\"10\"", "\"asset0In\":\"1e1\"", "plain decimal")]
        [TestCase("\"asset0In\":\"10\"", "\"asset0In\":\"-10\"", "plain decimal")]
        [TestCase("\"priceNative\":\"0.15\"", "\"priceNative\":\"0\"", "priceNative")]
        [TestCase("\"priceNative\":\"0.15\"", "\"priceNative\":\"6.6\"", "does not match")]
        [TestCase("\"pairId\":\"1\"", "\"pairId\":\"\"", "pairId")]
        [TestCase("\"txnIndex\":1", "\"txnIndex\":-1", "txnIndex")]
        [TestCase("\"reserves\":{\"asset0\":\"5\",\"asset1\":\"7\"}", "\"reserves\":{\"asset0\":\"5\"}", "asset1")]
        [TestCase("\"maker\":\"M\"", "\"maker\":\"M\",\"surprise\":1", "unexpected property")]
        public void BrokenSwap_IsRejected(string find, string replace, string expectedFragment)
        {
            var json = GoodSwap.Replace(find, replace);
            Assert.That(json, Is.Not.EqualTo(GoodSwap));
            var errors = GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{json}]}}"), 100, 100);
            Assert.That(errors, Is.Not.Empty);
            Assert.That(string.Join(" | ", errors), Does.Contain(expectedFragment).IgnoreCase);
        }

        [Test]
        public void Ordering_Uniqueness_AndRange_AreChecked()
        {
            string Ev(ulong block, ulong txn, uint ev) => GoodSwap.Replace("\"blockNumber\":100", $"\"blockNumber\":{block}").Replace("\"txnIndex\":1,\"eventIndex\":0", $"\"txnIndex\":{txn},\"eventIndex\":{ev}");

            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{Ev(100, 2, 0)},{Ev(100, 1, 0)}]}}"), 100, 101), Has.Some.Contain("not sorted"));
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{Ev(100, 1, 0)},{Ev(100, 1, 0)}]}}"), 100, 101), Has.Some.Contain("not unique"));
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{Ev(99, 1, 0)}]}}"), 100, 101), Has.Some.Contain("outside the requested range"));
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{Ev(100, 1, 0)},{Ev(100, 1, 1)},{Ev(101, 1, 0)}]}}"), 100, 101), Is.Empty);
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse("{\"events\":[]}"), 1, 2), Is.Empty);
        }

        [Test]
        public void JoinAndExit_AreValidated()
        {
            const string join = """
                {"block":{"blockNumber":100,"blockTimestamp":1760000000},"eventType":"join","txnId":"T","txnIndex":1,"eventIndex":0,"maker":"M","pairId":"1",
                 "amount0":"2","amount1":"0.3","reserves":{"asset0":"1","asset1":"2"}}
                """;
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{join}]}}"), 100, 100), Is.Empty);
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{join.Replace("\"amount0\":\"2\",\"amount1\":\"0.3\"", "\"amount0\":\"0\",\"amount1\":\"0\"")}]}}"), 100, 100), Has.Some.Contain("both amounts 0"));
            Assert.That(GeckoTerminalSchema.ValidateEvents(Parse($"{{\"events\":[{join.Replace("\"amount0\":\"2\"", "\"amount0\":null")}]}}"), 100, 100), Is.Not.Empty);
        }

        [Test]
        public void LatestBlock_AssetAndPair_AreValidated()
        {
            Assert.That(GeckoTerminalSchema.ValidateLatestBlock(Parse("{\"block\":{\"blockNumber\":5,\"blockTimestamp\":1760000000}}")), Is.Empty);
            Assert.That(GeckoTerminalSchema.ValidateLatestBlock(Parse("{\"block\":{\"blockNumber\":5}}")), Is.Not.Empty);
            Assert.That(GeckoTerminalSchema.ValidateLatestBlock(Parse("{\"blockNumber\":5}")), Is.Not.Empty);

            Assert.That(GeckoTerminalSchema.ValidateAsset(Parse("{\"asset\":{\"id\":\"0\",\"name\":\"Algorand\",\"symbol\":\"ALGO\",\"decimals\":6,\"totalSupply\":\"10000000000\",\"metadata\":{\"type\":\"ASA\"}}}"), "0"), Is.Empty);
            Assert.That(GeckoTerminalSchema.ValidateAsset(Parse("{\"asset\":{\"id\":\"0\",\"name\":\"\",\"symbol\":\"ALGO\",\"decimals\":6}}"), "0"), Is.Not.Empty, "empty name");
            Assert.That(GeckoTerminalSchema.ValidateAsset(Parse("{\"asset\":{\"id\":\"1\",\"name\":\"x\",\"symbol\":\"X\",\"decimals\":6}}"), "0"), Is.Not.Empty, "wrong id");

            Assert.That(GeckoTerminalSchema.ValidatePair(Parse("{\"pair\":{\"id\":\"7\",\"dexKey\":\"biatec\",\"asset0Id\":\"0\",\"asset1Id\":\"31566704\",\"feeBps\":30}}"), "7"), Is.Empty);
            Assert.That(GeckoTerminalSchema.ValidatePair(Parse("{\"pair\":{\"id\":\"7\",\"dexKey\":\"biatec\",\"asset0Id\":\"0\",\"asset1Id\":\"0\"}}"), "7"), Is.Not.Empty, "same asset twice");
        }

        // ------------------------------------------------------------------------------------------ the service's own output

        private static async Task<(CoinGeckoService Service, FakeSource Source)> Arrange()
        {
            var pools = new MockPoolRepository();
            await pools.StorePoolAsync(MakePool(1));
            await pools.StorePoolAsync(MakePool(2, assetA: 31566704, assetB: 123)); // asset order other than "ALGO first"
            var source = new FakeSource();
            var trades = new List<AVMTradeReporter.Models.Data.Trade>();
            for (ulong block = 1000; block < 1040; block++)
            {
                trades.Add(Swap($"S{block}a", block, 1, 1));
                trades.Add(Swap($"S{block}b", block, 2, 1, zeroToOne: false));
                if (block % 5 == 0) trades.Add(Swap($"S{block}c", block, 3, 2, zeroToOne: false));
            }
            source.Add(trades);
            source.Add(new[] { Deposit("D1", 1003, 9, 1), Deposit("D2", 1017, 4, 1, exit: true) });
            return (Service(source, pools, new FakeTracker { Latest = new IndexedBlock(5000, 1_760_000_100) }), source);
        }

        [Test]
        public async Task ServiceOutput_IsConformant_ForSwapsJoinsAndExits()
        {
            var (service, _) = await Arrange();
            var result = await service.GetEventsJsonAsync(1000, 1039, default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));

            var root = Parse(System.Text.Encoding.UTF8.GetString(result.Value!));
            Assert.That(GeckoTerminalSchema.ValidateEvents(root, 1000, 1039), Is.Empty);
            var types = root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).Distinct().OrderBy(t => t).ToArray();
            Assert.That(types, Is.EqualTo(new[] { "exit", "join", "swap" }));
        }

        [Test]
        public async Task ServiceOutput_IsConformant_AtEveryBoundary()
        {
            var (service, _) = await Arrange();
            foreach (var (from, to) in new (ulong, ulong)[] { (1000, 1000), (1039, 1039), (999, 1000), (1039, 1040), (1003, 1003), (1017, 1017), (1, 999) })
            {
                var result = await service.GetEventsJsonAsync(from, to, default);
                Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), $"{from}-{to}");
                var errors = GeckoTerminalSchema.ValidateEvents(Parse(System.Text.Encoding.UTF8.GetString(result.Value!)), from, to);
                Assert.That(errors, Is.Empty, $"{from}-{to}: {string.Join("; ", errors)}");
            }
        }

        [Test]
        public async Task RangeSplit_ConcatenatesToTheWholeRange_BothBoundsInclusive()
        {
            var (service, _) = await Arrange();
            async Task<string[]> Ids(ulong from, ulong to)
            {
                var root = Parse(System.Text.Encoding.UTF8.GetString((await service.GetEventsJsonAsync(from, to, default)).Value!));
                return root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("txnId").GetString() + "/" + e.GetProperty("eventType").GetString() + "/" + e.GetProperty("txnIndex")).ToArray();
            }

            var whole = await Ids(1000, 1039);
            var halves = (await Ids(1000, 1019)).Concat(await Ids(1020, 1039)).ToArray();
            Assert.That(halves, Is.EqualTo(whole), "a poller that walks the chain in slices must see exactly the events of the whole range");
        }

        [Test]
        public async Task SameRangeTwice_IsByteIdentical()
        {
            var (service, _) = await Arrange();
            var first = (await service.GetEventsJsonAsync(1000, 1039, default)).Value!;
            var second = (await service.GetEventsJsonAsync(1000, 1039, default)).Value!;
            Assert.That(second, Is.EqualTo(first), "an immutable range must never change between requests");
        }

        [Test]
        public async Task AssetAndPair_AreConformant()
        {
            var (service, _) = await Arrange();
            var pair = await service.GetPairAsync("1", default);
            Assert.That(GeckoTerminalSchema.ValidatePair(Parse(JsonSerializer.Serialize(pair.Value)), "1"), Is.Empty);
            var flipped = await service.GetPairAsync("2", default);
            Assert.That(flipped.Value!.Pair.Asset0Id, Is.EqualTo("31566704"), "asset0/asset1 follow the on-chain order of the pool, whatever it is");
            foreach (var id in new[] { "0", "31566704", "123" })
            {
                var asset = await service.GetAssetAsync(id, default);
                Assert.That(asset.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), id);
                Assert.That(GeckoTerminalSchema.ValidateAsset(Parse(JsonSerializer.Serialize(asset.Value)), id), Is.Empty);
            }
        }

        [Test]
        public async Task LatestBlock_IsConformant()
        {
            var (service, _) = await Arrange();
            var latest = await service.GetLatestBlockAsync(default);
            Assert.That(GeckoTerminalSchema.ValidateLatestBlock(Parse(JsonSerializer.Serialize(latest.Value))), Is.Empty);
        }
    }
}
