using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AVMTradeReporterTests.Conformance
{
    /// <summary>
    /// Live conformance + smoke + benchmark suite for a DEPLOYED <c>/api/coingecko</c>. It is what stands between a deploy and
    /// GeckoTerminal: it validates every response against the spec (<see cref="GeckoTerminalSchema"/>), behaves like their
    /// indexer (polling simulation), checks the error contract, compares known historical events against golden data and
    /// holds latency budgets. Skipped unless <c>GECKOTERMINAL_BASE_URL</c> is set, so it never runs in the normal test run:
    /// <code>
    /// GECKOTERMINAL_BASE_URL=https://api.testnet.scan.biatec.io dotnet test --filter Category=Conformance
    /// </code>
    /// Optional: GECKOTERMINAL_GOLDEN (name of Conformance/golden/*.json), GECKOTERMINAL_POLL_SECONDS (default 40),
    /// GECKOTERMINAL_SCAN_CHUNKS (default 80 x 1000 blocks looked back for events), GECKOTERMINAL_EXPECT_EVENTS (true/false,
    /// default true: finding no event at all in the scanned window fails), GECKOTERMINAL_LATENCY_FACTOR (default 1).
    /// </summary>
    [Category("Conformance")]
    [NonParallelizable]
    public class GeckoTerminalConformanceTests
    {
        private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("GECKOTERMINAL_BASE_URL")?.TrimEnd('/');
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private static double LatencyFactor => double.TryParse(Environment.GetEnvironmentVariable("GECKOTERMINAL_LATENCY_FACTOR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1;
        private static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

        [OneTimeSetUp]
        public async Task WaitUntilServing()
        {
            if (string.IsNullOrEmpty(BaseUrl)) Assert.Ignore("GECKOTERMINAL_BASE_URL is not set - the live conformance suite runs against a deployed API only");
            // right after a rollout the new pod may still be warming up: wait (bounded) until latest-block answers
            var deadline = DateTime.UtcNow.AddMinutes(EnvInt("GECKOTERMINAL_WAIT_MINUTES", 5));
            string last = "";
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var (status, body, _) = await Get("latest-block");
                    if (status == HttpStatusCode.OK) return;
                    last = $"{(int)status} {body}";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    last = ex.Message;
                }
                await Task.Delay(5_000);
            }
            Assert.Fail($"{BaseUrl}/api/coingecko/latest-block never answered 200 within the wait window (last: {last})");
        }

        // ------------------------------------------------------------------------------------------------ helpers

        private static async Task<(HttpStatusCode Status, string Body, TimeSpan Elapsed)> Get(string path)
        {
            var watch = Stopwatch.StartNew();
            using var response = await Http.GetAsync($"{BaseUrl}/api/coingecko/{path}");
            var body = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, body, watch.Elapsed);
        }

        private static async Task<JsonElement> GetOk(string path)
        {
            var (status, body, _) = await Get(path);
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"GET {path} -> {(int)status}: {Truncate(body)}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

        private static async Task<(ulong Block, long Timestamp)> LatestBlock()
        {
            var root = await GetOk("latest-block");
            Assert.That(GeckoTerminalSchema.ValidateLatestBlock(root), Is.Empty);
            var block = root.GetProperty("block");
            return (block.GetProperty("blockNumber").GetUInt64(), block.GetProperty("blockTimestamp").GetInt64());
        }

        private static void AssertConformant(List<string> errors, string what)
            => Assert.That(errors, Is.Empty, $"{what} violates the GeckoTerminal schema:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", errors.Take(15)));

        /// <summary>Walks back from latest-block in 1000-block slices until a slice holds events; (0,0) when none does.</summary>
        private static async Task<(ulong From, ulong To, JsonElement Root)?> FindRangeWithEvents()
        {
            var latest = (await LatestBlock()).Block;
            var chunks = EnvInt("GECKOTERMINAL_SCAN_CHUNKS", 80);
            for (var i = 0; i < chunks; i++)
            {
                var to = latest - (ulong)i * 1000;
                if (to < 1000) break;
                var from = to - 999;
                var root = await GetOk($"events?fromBlock={from}&toBlock={to}");
                if (root.GetProperty("events").GetArrayLength() > 0) return (from, to, root);
            }
            return null;
        }

        /// <summary>A quiet network (a testnet) may have had no trade for weeks: its recorded golden range is real data that always has events.</summary>
        private static async Task<(ulong From, ulong To, JsonElement Root)?> GoldenRangeWithEvents()
        {
            var name = Environment.GetEnvironmentVariable("GECKOTERMINAL_GOLDEN");
            var path = string.IsNullOrEmpty(name) ? null : Path.Combine(AppContext.BaseDirectory, "Conformance", "golden", name + ".json");
            if (path == null || !File.Exists(path)) return null;
            var golden = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
            var c = golden.GetProperty("cases").EnumerateArray().FirstOrDefault(c => c.GetProperty("events").GetArrayLength() > 0);
            if (c.ValueKind != JsonValueKind.Object) return null;
            var from = c.GetProperty("fromBlock").GetUInt64();
            var to = c.GetProperty("toBlock").GetUInt64();
            TestContext.Out.WriteLine($"no recent events - using the golden range {from}-{to}");
            return (from, to, await GetOk($"events?fromBlock={from}&toBlock={to}"));
        }

        private static async Task<(ulong From, ulong To, JsonElement Root)> RequireRangeWithEvents()
        {
            var found = await FindRangeWithEvents() ?? await GoldenRangeWithEvents();
            if (found == null)
            {
                var expect = !string.Equals(Environment.GetEnvironmentVariable("GECKOTERMINAL_EXPECT_EVENTS"), "false", StringComparison.OrdinalIgnoreCase);
                if (expect) Assert.Fail("no event found in the scanned window - the integration is serving nothing (set GECKOTERMINAL_EXPECT_EVENTS=false for a network without trades)");
                Assert.Ignore("no events in the scanned window");
            }
            return found!.Value;
        }

        // ------------------------------------------------------------------------------------------------ latest-block

        [Test, Order(1)]
        public async Task LatestBlock_IsConformant_AndFresh()
        {
            var (block, timestamp) = await LatestBlock();
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp);
            TestContext.Out.WriteLine($"latest-block {block} is {age.TotalSeconds:F0} s old");
            Assert.That(age, Is.LessThan(TimeSpan.FromMinutes(5)), "latest-block is stale: the indexer stopped, or the watermark is stuck");
            Assert.That(age, Is.GreaterThan(TimeSpan.FromSeconds(-30)), "latest-block timestamp lies in the future");
        }

        [Test, Order(2)]
        public async Task LatestBlock_Advances_AndNeverMovesBackwards()
        {
            var first = await LatestBlock();
            ulong previous = first.Block;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            var advanced = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2_000);
                var now = await LatestBlock();
                Assert.That(now.Block, Is.GreaterThanOrEqualTo(previous), "latest-block went backwards");
                if (now.Block > first.Block) advanced = true;
                previous = now.Block;
                if (advanced && DateTime.UtcNow > deadline.AddSeconds(-30)) break;
            }
            Assert.That(advanced, Is.True, "latest-block did not advance in 45 s: the indexer is not completing blocks");
        }

        // ------------------------------------------------------------------------------------------------ events

        [Test, Order(3)]
        public async Task Events_OfARealRange_AreConformant_AndEveryReferencedPairAndAssetResolves()
        {
            var (from, to, root) = await RequireRangeWithEvents();
            AssertConformant(GeckoTerminalSchema.ValidateEvents(root, from, to), $"events {from}-{to}");

            var events = root.GetProperty("events").EnumerateArray().ToList();
            TestContext.Out.WriteLine($"{events.Count} events in {from}-{to}, types: " + string.Join(", ", events.GroupBy(e => e.GetProperty("eventType").GetString()).Select(g => $"{g.Key}={g.Count()}")));

            foreach (var pairId in events.Select(e => e.GetProperty("pairId").GetString()!).Distinct())
            {
                var pair = await GetOk($"pair?id={pairId}");
                AssertConformant(GeckoTerminalSchema.ValidatePair(pair, pairId), $"pair {pairId}");
                foreach (var assetId in new[] { pair.GetProperty("pair").GetProperty("asset0Id").GetString()!, pair.GetProperty("pair").GetProperty("asset1Id").GetString()! })
                {
                    var asset = await GetOk($"asset?id={assetId}");
                    AssertConformant(GeckoTerminalSchema.ValidateAsset(asset, assetId), $"asset {assetId}");
                }
            }
        }

        [Test, Order(4)]
        public async Task Events_SwapAmounts_StayWithinTheAssetDecimals()
        {
            var (_, _, root) = await RequireRangeWithEvents();
            var decimals = new Dictionary<string, int>();
            async Task<int> Decimals(string assetId)
            {
                if (!decimals.TryGetValue(assetId, out var d))
                    decimals[assetId] = d = (await GetOk($"asset?id={assetId}")).GetProperty("asset").GetProperty("decimals").GetInt32();
                return d;
            }
            static int Fraction(string s) => s.Contains('.') ? s.Length - s.IndexOf('.') - 1 : 0;

            foreach (var e in root.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("eventType").GetString() == "swap").Take(40))
            {
                var pair = (await GetOk($"pair?id={e.GetProperty("pairId").GetString()}")).GetProperty("pair");
                var d0 = await Decimals(pair.GetProperty("asset0Id").GetString()!);
                var d1 = await Decimals(pair.GetProperty("asset1Id").GetString()!);
                foreach (var (name, dec) in new[] { ("asset0In", d0), ("asset0Out", d0), ("asset1In", d1), ("asset1Out", d1) })
                {
                    if (e.TryGetProperty(name, out var v)) Assert.That(Fraction(v.GetString()!), Is.LessThanOrEqualTo(dec), $"{e.GetProperty("txnId")} {name}={v} has more decimals than the asset ({dec}): not decimalized correctly");
                }
            }
        }

        [Test, Order(5)]
        public async Task Events_SplitRange_EqualsTheWholeRange_AndRepeatedRequestsAreIdentical()
        {
            var (from, to, root) = await RequireRangeWithEvents();
            var mid = from + (to - from) / 2;
            string[] Keys(JsonElement r) => r.GetProperty("events").EnumerateArray().Select(e => $"{e.GetProperty("block").GetProperty("blockNumber")}/{e.GetProperty("txnIndex")}/{e.GetProperty("eventIndex")}/{e.GetProperty("txnId")}").ToArray();

            var left = await GetOk($"events?fromBlock={from}&toBlock={mid}");
            var right = await GetOk($"events?fromBlock={mid + 1}&toBlock={to}");
            Assert.That(Keys(left).Concat(Keys(right)), Is.EqualTo(Keys(root)), "a poller walking the chain in slices must see exactly what the whole range holds (both bounds inclusive)");

            var (_, bodyA, _) = await Get($"events?fromBlock={from}&toBlock={to}");
            var (_, bodyB, _) = await Get($"events?fromBlock={from}&toBlock={to}");
            Assert.That(bodyB, Is.EqualTo(bodyA), "an immutable range must never change between requests");

            // a single block range returns exactly the events of that block
            var firstBlock = root.GetProperty("events")[0].GetProperty("block").GetProperty("blockNumber").GetUInt64();
            var single = await GetOk($"events?fromBlock={firstBlock}&toBlock={firstBlock}");
            Assert.That(single.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("block").GetProperty("blockNumber").GetUInt64()).Distinct(), Is.EqualTo(new[] { firstBlock }));
            AssertConformant(GeckoTerminalSchema.ValidateEvents(single, firstBlock, firstBlock), "single block");
        }

        [Test, Order(6)]
        public async Task Events_NewestBlocks_AreConformant_UpToTheLatestBlock()
        {
            var latest = (await LatestBlock()).Block;
            var from = latest - 20;
            var root = await GetOk($"events?fromBlock={from}&toBlock={latest}");
            AssertConformant(GeckoTerminalSchema.ValidateEvents(root, from, latest), $"events {from}-{latest}");
        }

        // ------------------------------------------------------------------------------------------------ error contract

        [Test, Order(7)]
        public async Task ErrorContract_IsRetryableOrExplicit()
        {
            var latest = (await LatestBlock()).Block;
            async Task Expect(string path, HttpStatusCode expected)
            {
                var (status, body, _) = await Get(path);
                Assert.That(status, Is.EqualTo(expected), $"GET {path}: {Truncate(body)}");
                if (!string.IsNullOrEmpty(body)) Assert.That(JsonDocument.Parse(body).RootElement.TryGetProperty("error", out _), Is.True, $"GET {path}: error bodies carry {{\"error\": ...}}");
            }

            await Expect("events", HttpStatusCode.BadRequest);
            await Expect("events?fromBlock=10&toBlock=5", HttpStatusCode.BadRequest);
            await Expect("events?fromBlock=1&toBlock=1000000", HttpStatusCode.BadRequest);
            await Expect($"events?fromBlock={latest - 1}&toBlock={latest + 100}", HttpStatusCode.ServiceUnavailable); // beyond latest-block: retryable, never partial
            await Expect("asset?id=abc", HttpStatusCode.BadRequest);
            await Expect("asset?id=-1", HttpStatusCode.BadRequest);
            await Expect("pair?id=abc", HttpStatusCode.BadRequest);
            await Expect("pair?id=1", HttpStatusCode.NotFound);
            await Expect("asset?id=18446744073709551615", HttpStatusCode.NotFound);

            var (status503, _, _) = await Get($"events?fromBlock={latest - 1}&toBlock={latest + 100}");
            Assert.That(status503, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test, Order(8)]
        public async Task Endpoints_NeedNoAuthentication_AndSendSensibleHeaders()
        {
            using var response = await Http.GetAsync($"{BaseUrl}/api/coingecko/latest-block");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "CoinGecko's indexer cannot sign ARC-14 tokens: the endpoints must be anonymous");
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(response.Headers.CacheControl?.MaxAge, Is.Not.Null.And.LessThanOrEqualTo(TimeSpan.FromSeconds(5)), "latest-block must stay fresh");
        }

        // ------------------------------------------------------------------------------------------------ golden data

        [Test, Order(9)]
        public async Task GoldenEvents_AreReproducedExactly()
        {
            var name = Environment.GetEnvironmentVariable("GECKOTERMINAL_GOLDEN");
            if (string.IsNullOrEmpty(name)) Assert.Ignore("GECKOTERMINAL_GOLDEN is not set");
            var path = Path.Combine(AppContext.BaseDirectory, "Conformance", "golden", name + ".json");
            if (!File.Exists(path)) Assert.Fail($"golden file {path} not found");

            var golden = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
            foreach (var c in golden.GetProperty("cases").EnumerateArray())
            {
                var from = c.GetProperty("fromBlock").GetUInt64();
                var to = c.GetProperty("toBlock").GetUInt64();
                var actual = await GetOk($"events?fromBlock={from}&toBlock={to}");
                var expected = c.GetProperty("events");
                // compared as canonical JSON: property order is not part of the contract, values are
                Assert.That(Canonical(actual.GetProperty("events")), Is.EqualTo(Canonical(expected)), $"golden case {c.GetProperty("name")} ({from}-{to}) changed - a historical event must never change");
            }
            foreach (var p in golden.GetProperty("pairs").EnumerateArray())
            {
                var id = p.GetProperty("id").GetString()!;
                Assert.That(Canonical((await GetOk($"pair?id={id}")).GetProperty("pair")), Is.EqualTo(Canonical(p)), $"pair {id} changed - asset0/asset1 order and fee must be immutable");
            }
        }

        private static string Canonical(JsonElement e)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                Write(e, w);
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());

            static void Write(JsonElement el, Utf8JsonWriter w)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        w.WriteStartObject();
                        foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); Write(p.Value, w); }
                        w.WriteEndObject();
                        break;
                    case JsonValueKind.Array:
                        w.WriteStartArray();
                        foreach (var i in el.EnumerateArray()) Write(i, w);
                        w.WriteEndArray();
                        break;
                    default:
                        el.WriteTo(w);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ polling simulation

        /// <summary>
        /// Behaves like GeckoTerminal's indexer: every 2 s read latest-block, then request events(previous+1 .. latest) - never
        /// a gap, never an overlap - and validate each answer. Any 4xx, any 5xx after warm-up, a gap or a schema violation is
        /// what would halt their indexing of the DEX.
        /// </summary>
        [Test, Order(10)]
        public async Task PollingSimulation_FollowsTheChain_WithoutGapsOrErrors()
        {
            var seconds = EnvInt("GECKOTERMINAL_POLL_SECONDS", 40);
            var (cursor, _) = await LatestBlock();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var cycles = 0;
            var eventsSeen = 0;
            ulong covered = cursor; // highest block whose events were requested

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2_000);
                var (latest, _) = await LatestBlock();
                cycles++;
                if (latest <= covered) continue;

                var from = covered + 1;
                while (from <= latest)
                {
                    var to = Math.Min(latest, from + 999);
                    var root = await GetOk($"events?fromBlock={from}&toBlock={to}");
                    AssertConformant(GeckoTerminalSchema.ValidateEvents(root, from, to), $"poll events {from}-{to}");
                    eventsSeen += root.GetProperty("events").GetArrayLength();
                    covered = to;
                    from = to + 1;
                }
            }
            TestContext.Out.WriteLine($"{cycles} polling cycles, followed the chain from {cursor} to {covered}, {eventsSeen} events");
            Assert.That(covered, Is.GreaterThan(cursor), "no block was polled: latest-block did not move");
        }

        // ------------------------------------------------------------------------------------------------ latency budgets

        private static async Task<List<double>> Time(int count, Func<int, string> path)
        {
            var samples = new List<double>();
            for (var i = 0; i < count; i++)
            {
                var (status, body, elapsed) = await Get(path(i));
                Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"{path(i)}: {Truncate(body)}");
                samples.Add(elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return samples;
        }

        private static double Percentile(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

        [Test, Order(11)]
        public async Task Latency_Budgets_Hold()
        {
            var factor = LatencyFactor;
            var latest = (await LatestBlock()).Block;

            var latestBlock = await Time(40, _ => "latest-block");
            var warmEvents = await Time(1, _ => $"events?fromBlock={latest - 500}&toBlock={latest - 100}"); // fills the cache
            var cachedEvents = await Time(20, _ => $"events?fromBlock={latest - 500}&toBlock={latest - 100}");
            var coldEvents = await Time(5, i => $"events?fromBlock={latest - 5_000 - (ulong)i * 1_000 - 999}&toBlock={latest - 5_000 - (ulong)i * 1_000}");
            var pairs = await Time(10, _ => "asset?id=0");

            TestContext.Out.WriteLine($"latest-block p95 {Percentile(latestBlock, .95):F0} ms | cached events p95 {Percentile(cachedEvents, .95):F0} ms | cold 1000-block events max {coldEvents.Max():F0} ms | asset p95 {Percentile(pairs, .95):F0} ms");
            Assert.That(Percentile(latestBlock, .95), Is.LessThan(800 * factor), "latest-block is polled every 2 s - it must be a memory read");
            Assert.That(Percentile(cachedEvents, .95), Is.LessThan(800 * factor), "a cached immutable range must not hit storage");
            Assert.That(coldEvents.Max(), Is.LessThan(8_000 * factor), "an uncached 1000-block range");
            Assert.That(Percentile(pairs, .95), Is.LessThan(800 * factor), "asset/pair never touch storage");
        }

        [Test, Order(12)]
        public async Task Load_FiftyConcurrentPollers_AreServedWithoutErrorsOrRateLimiting()
        {
            var latest = (await LatestBlock()).Block;
            var watch = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
            {
                var (s1, _, _) = await Get("latest-block");
                var (s2, _, _) = await Get($"events?fromBlock={latest - 50}&toBlock={latest - 10}");
                return (s1, s2);
            }));
            watch.Stop();
            TestContext.Out.WriteLine($"50 concurrent pollers (100 requests): {watch.ElapsedMilliseconds} ms");
            Assert.That(results.Select(r => r.s1).Concat(results.Select(r => r.s2)), Has.All.EqualTo(HttpStatusCode.OK), "no 429 / 5xx under 50 concurrent pollers");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20 * LatencyFactor)));
        }

        [Test, Order(13)]
        public async Task RateLimit_AllowsTheIndexersPollingRate()
        {
            // GeckoTerminal polls ~every 2 s: 2 requests per cycle = 60/min, plus a burst of asset/pair lookups on first sight.
            // 150 requests in one go must pass - the 60/min anonymous budget of the rest of the API would not.
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 150; i++) statuses.Add((await Get("latest-block")).Status);
            Assert.That(statuses.Count(s => s == HttpStatusCode.TooManyRequests), Is.Zero, "429: the CoinGecko rate-limit bucket is missing or too small");
            Assert.That(statuses, Has.All.EqualTo(HttpStatusCode.OK));
        }
    }
}
