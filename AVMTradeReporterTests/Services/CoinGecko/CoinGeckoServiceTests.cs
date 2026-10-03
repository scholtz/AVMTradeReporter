using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class CoinGeckoServiceTests
    {
        private const ulong PoolAppId = 3136517663;
        private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

        private sealed class FakeTracker : IIndexedBlockTracker
        {
            public IndexedBlock? Latest { get; set; }
            public IndexedBlock? CompletedThrough => Latest;
            public void Seed(ulong completedRound, long? unixTimestamp, bool verified = true) => Latest = new IndexedBlock(completedRound, unixTimestamp ?? 0);
            public void MarkCompleted(ulong round, long? unixTimestamp) => Latest = new IndexedBlock(round, unixTimestamp ?? 0);
            public Task<IndexedBlock?> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(Latest);
        }

        private sealed class FakeSource : ICoinGeckoEventSource
        {
            public List<Trade> Trades { get; } = new();
            public List<Liquidity> Liquidity { get; } = new();
            public bool IsAvailable { get; set; } = true;
            public Exception? Failure { get; set; }
            public TimeSpan QueryDelay { get; set; }
            public int TradeCalls;
            private int _inFlight;
            public int MaxInFlight;
            public int LiquidityCalls;
            public List<(ulong Lo, ulong Hi, int Size)> TradeQueries { get; } = new();

            public Task<IReadOnlyList<Trade>> GetTradesAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref TradeCalls);
                if (Failure != null) throw Failure;
                lock (TradeQueries) TradeQueries.Add((lo, hi, size));
                return RunAsync(() => (IReadOnlyList<Trade>)Trades.Where(t => t.BlockId >= lo && t.BlockId <= hi).OrderBy(t => t.BlockId).Take(size).ToList());
            }

            public Task<IReadOnlyList<Liquidity>> GetLiquidityAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref LiquidityCalls);
                return RunAsync(() => (IReadOnlyList<Liquidity>)Liquidity.Where(t => t.BlockId >= lo && t.BlockId <= hi).OrderBy(t => t.BlockId).Take(size).ToList());
            }

            private async Task<T> RunAsync<T>(Func<T> produce)
            {
                var now = Interlocked.Increment(ref _inFlight);
                int seen;
                while ((seen = MaxInFlight) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
                try
                {
                    if (QueryDelay > TimeSpan.Zero) await Task.Delay(QueryDelay);
                    return produce();
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        /// <summary>Every clock the service compares reads this - tests advance it instead of sleeping.</summary>
        private sealed class FakeTime : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private FakeTime _time = null!;
        private FakeTracker _tracker = null!;
        private FakeSource _source = null!;
        private MockPoolRepository _pools = null!;
        private Mock<IAssetRepository> _assets = null!;
        private AppConfiguration _config = null!;

        [SetUp]
        public async Task SetUp()
        {
            _time = new FakeTime();
            _tracker = new FakeTracker { Latest = new IndexedBlock(1000, 1_760_000_100) };
            _source = new FakeSource();
            _pools = new MockPoolRepository();
            _config = new AppConfiguration();
            _config.Redis.Enabled = false;
            _config.CoinGecko.ElasticPageSize = 1000;
            _config.CoinGecko.TransientMemoSeconds = 0; // the memo uses the real clock; tested on its own below

            _assets = new Mock<IAssetRepository>();
            _assets.Setup(a => a.GetAssetAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ulong id, CancellationToken _) => id == 12345 ? null : Asset(id, "Name" + id, "U" + id, 6, 1_000_000_000_000));

            await _pools.StorePoolAsync(Pool(PoolAppId, "POOLADDR", 0, 31566704));
        }

        private static BiatecAsset Asset(ulong id, string name, string unit, ulong decimals, ulong total) => new()
        {
            Index = id,
            Type = AssetType.ASA,
            Params = new Algorand.Algod.Model.AssetParams { Name = name, UnitName = unit, Decimals = decimals, Total = total, Url = "https://x.test" },
        };

        private static PoolModel Pool(ulong appId, string address, ulong a, ulong b, DEXProtocol protocol = DEXProtocol.Biatec) => new()
        {
            PoolAddress = address,
            PoolAppId = appId,
            AssetIdA = a,
            AssetIdB = b,
            AssetADecimals = 6,
            AssetBDecimals = 6,
            Protocol = protocol,
            LPFee = 0.003m,
            Timestamp = Time,
        };

        private CoinGeckoService Create(Action<CoinGeckoConfiguration>? tweak = null)
        {
            tweak?.Invoke(_config.CoinGecko);
            return new CoinGeckoService(Options.Create(_config), _tracker, _source, _pools, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);
        }

        private static Trade Swap(string txId, ulong block, ulong? txn = null, uint? ev = null, ulong appId = PoolAppId, ulong amountIn = 10_000_000, ulong amountOut = 1_500_000) => new()
        {
            AssetIdIn = 0,
            AssetIdOut = 31566704,
            AssetAmountIn = amountIn,
            AssetAmountOut = amountOut,
            TxId = txId,
            TopTxId = "TOP-" + txId,
            BlockId = block,
            Timestamp = Time,
            Trader = "TRADER",
            PoolAppId = appId,
            A = 5_000_000_000,
            B = 7_000_000_000,
            TradeState = TxState.Confirmed,
            Protocol = DEXProtocol.Biatec,
            TxnIndex = txn,
            EventIndex = ev,
        };

        private sealed class Counter { public int Value; }

        /// <summary>A pool repository with one published pool that counts how often the pool cache is enumerated (and can be made to block).</summary>
        private static (Mock<IPoolRepository> Repo, Counter Loads) CountingPools(ManualResetEventSlim? gate = null)
        {
            var loads = new Counter();
            var pool = Pool(PoolAppId, "POOLADDR", 0, 31566704);
            var repo = new Mock<IPoolRepository>();
            repo.Setup(p => p.GetAllPoolAddresses()).Returns(() =>
            {
                Interlocked.Increment(ref loads.Value);
                gate?.Wait(); // blocks only while the test holds the gate closed
                return new[] { pool.PoolAddress };
            });
            repo.Setup(p => p.GetCachedPool(pool.PoolAddress)).Returns(pool);
            return (repo, loads);
        }

        private static JsonElement[] Events(CoinGeckoResult<byte[]> result)
        {
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), result.Error);
            using var doc = JsonDocument.Parse(result.Value!);
            return doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        // ----------------------------------------------------------------- latest-block

        [Test]
        public async Task LatestBlock_ReportsTheWatermark()
        {
            var result = await Create().GetLatestBlockAsync(default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            Assert.That(result.Value!.Block.BlockNumber, Is.EqualTo(1000UL));
            Assert.That(result.Value.Block.BlockTimestamp, Is.EqualTo(1_760_000_100L));
            Assert.That(JsonSerializer.Serialize(result.Value), Is.EqualTo("{\"block\":{\"blockNumber\":1000,\"blockTimestamp\":1760000100}}"));
        }

        [Test]
        public async Task LatestBlock_UnavailableUntilTheIndexerHasCompletedABlock()
        {
            _tracker.Latest = null;
            Assert.That((await Create().GetLatestBlockAsync(default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        // ----------------------------------------------------------------- asset

        [Test]
        public async Task Asset_IsDecimalizedAndNamed()
        {
            var result = await Create().GetAssetAsync("31566704", default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            var asset = result.Value!.Asset;
            Assert.That(asset.Id, Is.EqualTo("31566704"));
            Assert.That(asset.Name, Is.EqualTo("Name31566704"));
            Assert.That(asset.Symbol, Is.EqualTo("U31566704"));
            Assert.That(asset.Decimals, Is.EqualTo(6));
            Assert.That(asset.TotalSupply, Is.EqualTo("1000000"), "1e12 base units / 1e6");
            Assert.That(asset.Metadata!["url"], Is.EqualTo("https://x.test"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("abc")]
        [TestCase("-1")]
        [TestCase("1.5")]
        [TestCase("99999999999999999999999")]
        public async Task Asset_InvalidId_IsBadRequest(string? id)
        {
            Assert.That((await Create().GetAssetAsync(id, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
        }

        [Test]
        public async Task Asset_NotInAnyPublishedPool_IsNotFound_WithoutTouchingTheAssetRepository()
        {
            // an anonymous caller must not be able to make the service look up (and cache) arbitrary asset ids
            var service = Create();
            for (ulong id = 5000; id < 5050; id++) Assert.That((await service.GetAssetAsync(id.ToString(), default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            _assets.Verify(a => a.GetAssetAsync(It.Is<ulong>(id => id >= 5000 && id < 5050), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Asset_UnreadableRightNow_IsRetryable503_NotACached404()
        {
            // AssetRepository answers null for an algod timeout as well as for a destroyed asset
            await _pools.StorePoolAsync(Pool(902, "FLAKY", 0, 12345));
            var service = Create();
            Assert.That((await service.GetAssetAsync("12345", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await service.GetAssetAsync("12345", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            _assets.Verify(a => a.GetAssetAsync(12345, It.IsAny<CancellationToken>()), Times.Exactly(2), "not cached - it may succeed next time");
        }

        [Test]
        public async Task Asset_DestroyedAssetOfAPublishedPool_IsNotFound_AndNotAskedAgainWithinTheCacheWindow()
        {
            await _pools.StorePoolAsync(Pool(902, "DESTROYED", 0, 12345));
            _assets.Setup(a => a.IsDeletedAsync(12345, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var service = Create();
            Assert.That((await service.GetAssetAsync("12345", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            Assert.That((await service.GetAssetAsync("12345", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            _assets.Verify(a => a.GetAssetAsync(12345, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Asset_IsCached()
        {
            var service = Create();
            await service.GetAssetAsync("0", default);
            await service.GetAssetAsync("0", default);
            _assets.Verify(a => a.GetAssetAsync(0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Asset_Algo_SupplyIsInWholeUnits_BecauseTheNativeTokenIsStoredThatWay()
        {
            // AssetRepository synthesizes ALGO with Total = 10_000_000_000 whole ALGO next to Decimals = 6
            _assets.Setup(a => a.GetAssetAsync(0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Asset(0, "Algorand", "ALGO", 6, 10_000_000_000));
            var result = await Create().GetAssetAsync("0", default);
            Assert.That(result.Value!.Asset.TotalSupply, Is.EqualTo("10000000000"));
            Assert.That(result.Value.Asset.Decimals, Is.EqualTo(6));
        }

        [Test]
        public void MapAsset_FallsBackWhenNameOrUnitMissing_AndSkipsDestroyedAssets()
        {
            var noName = Asset(5, "", "", 2, 100);
            var mapped = CoinGeckoService.MapAsset(5, noName)!;
            Assert.That(mapped.Name, Is.EqualTo("Asset 5"));
            Assert.That(mapped.Symbol, Is.EqualTo("Asset 5"));
            Assert.That(mapped.TotalSupply, Is.EqualTo("1"));

            var unitOnly = Asset(6, "", "TKN", 0, 7);
            Assert.That(CoinGeckoService.MapAsset(6, unitOnly)!.Name, Is.EqualTo("TKN"));

            var deleted = Asset(8, "x", "x", 0, 1);
            deleted.Deleted = true;
            Assert.That(CoinGeckoService.MapAsset(8, deleted), Is.Null);
            Assert.That(CoinGeckoService.MapAsset(9, null), Is.Null);
        }

        // ----------------------------------------------------------------- pair

        [Test]
        public async Task Pair_UsesOnChainAssetOrder_AndFeeInBasisPoints()
        {
            var result = await Create().GetPairAsync(PoolAppId.ToString(), default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            var pair = result.Value!.Pair;
            Assert.That(pair.Id, Is.EqualTo("3136517663"));
            Assert.That(pair.DexKey, Is.EqualTo("biatec"));
            Assert.That(pair.Asset0Id, Is.EqualTo("0"));
            Assert.That(pair.Asset1Id, Is.EqualTo("31566704"));
            Assert.That(JsonSerializer.Serialize(pair), Does.Contain("\"feeBps\":30}").Or.Contain("\"feeBps\":30,"));
        }

        [Test]
        public async Task Pair_UnknownAndBadIds()
        {
            var service = Create();
            Assert.That((await service.GetPairAsync("42", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            Assert.That((await service.GetPairAsync("x", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
            Assert.That((await service.GetPairAsync(null, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
        }

        [Test]
        public async Task Pair_OtherDexesAreNotPublished()
        {
            await _pools.StorePoolAsync(Pool(777, "PACTADDR", 0, 31566704, DEXProtocol.Pact));
            await _pools.StorePoolAsync(Pool(778, "SCAMADDR", 0, 31566704, DEXProtocol.Scam));
            var service = Create();
            Assert.That((await service.GetPairAsync("777", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            Assert.That((await service.GetPairAsync("778", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
        }

        [Test]
        public async Task Pair_HighScamRatingIsHidden()
        {
            var pool = Pool(779, "SUSPECT", 0, 31566704);
            pool.ScamRating = 90;
            await _pools.StorePoolAsync(pool);
            Assert.That((await Create().GetPairAsync("779", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
        }

        [Test]
        public async Task Pair_DecimalsFallBackToTheAssetRepository()
        {
            var pool = Pool(780, "NODEC", 0, 31566704);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            Assert.That((await Create().GetPairAsync("780", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
        }

        [Test]
        public async Task Pair_ExistingPoolThatCannotBeDescribedRightNow_IsRetryable503_NotNotFound()
        {
            // decimals unknown on the pool and the asset repository cannot answer (id 12345) -> transient, not "unknown"
            var pool = Pool(901, "TRANSIENT", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            Assert.That((await Create().GetPairAsync("901", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        [Test]
        public async Task Pair_NewPoolIsFoundAfterTheSnapshotWasTaken()
        {
            var service = Create(c => c.PoolSnapshotSeconds = 3600);
            Assert.That((await service.GetPairAsync(PoolAppId.ToString(), default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            await _pools.StorePoolAsync(Pool(900, "NEW", 0, 31566704));
            // snapshot is older than the rate limit for "missing pool" refreshes only after a short while
            _time.Advance(TimeSpan.FromSeconds(6)); // past MissingPoolRefreshInterval
            Assert.That((await service.GetPairAsync("900", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
        }

        // ----------------------------------------------------------------- events validation

        [Test]
        public async Task Events_RejectInvalidRanges()
        {
            var service = Create(c => c.MaxBlockSpan = 10);
            Assert.That((await service.GetEventsJsonAsync(20, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest), "to < from");
            Assert.That((await service.GetEventsJsonAsync(1, 11, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest), "11 blocks > 10");
            Assert.That((await service.GetEventsJsonAsync(1, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), "exactly 10 blocks is allowed");
            Assert.That((await service.GetEventsJsonAsync(5, 5, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), "both bounds inclusive, a single block is a range");
        }

        [Test]
        public async Task Events_NeverServeBlocksBeyondLatestBlock()
        {
            var service = Create();
            var result = await service.GetEventsJsonAsync(1000, 1001, default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable), "retryable: another replica may simply be a block ahead");
            Assert.That(result.Error, Does.Contain("latest"));
            Assert.That(_source.TradeCalls, Is.Zero, "must not even query data that may be incomplete");
        }

        [Test]
        public async Task Events_UnavailableWithoutWatermarkOrStorage()
        {
            _tracker.Latest = null;
            Assert.That((await Create().GetEventsJsonAsync(1, 2, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));

            _tracker.Latest = new IndexedBlock(1000, 1);
            _source.IsAvailable = false;
            Assert.That((await Create().GetEventsJsonAsync(1, 2, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await Create().GetLatestBlockAsync(default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable), "a watermark without event storage would mislead the consumer");
        }

        [Test]
        public async Task Disabled_IsUnavailable()
        {
            var service = Create(c => c.Enabled = false);
            Assert.That((await service.GetLatestBlockAsync(default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await service.GetEventsJsonAsync(1, 2, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        // ----------------------------------------------------------------- events content

        [Test]
        public async Task Events_EmptyRange_IsAnEmptyList_NotAnError()
        {
            var events = Events(await Create().GetEventsJsonAsync(10, 20, default));
            Assert.That(events, Is.Empty);
        }

        [Test]
        public async Task Events_MapSwapsAndLiquidity_SortedByBlockTxnAndEvent()
        {
            _source.Trades.Add(Swap("S3", 12, txn: 9, ev: 0));
            _source.Trades.Add(Swap("S1", 10, txn: 4, ev: 1));
            _source.Trades.Add(Swap("S2", 10, txn: 4, ev: 0));
            _source.Liquidity.Add(new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                AssetIdA = 0,
                AssetIdB = 31566704,
                AssetAmountA = 2_000_000,
                AssetAmountB = 300_000,
                A = 1_000_000_000,
                B = 2_000_000_000,
                TxId = "L1",
                TopTxId = "TOP-L1",
                BlockId = 10,
                Timestamp = Time,
                LiquidityProvider = "LP",
                PoolAppId = PoolAppId,
                TxnIndex = 2,
                EventIndex = 0,
            });

            var events = Events(await Create().GetEventsJsonAsync(10, 12, default));

            Assert.That(events.Select(e => e.GetProperty("eventType").GetString()), Is.EqualTo(new[] { "join", "swap", "swap", "swap" }));
            Assert.That(events.Select(e => e.GetProperty("txnId").GetString()), Is.EqualTo(new[] { "TOP-L1", "TOP-S2", "TOP-S1", "TOP-S3" }));
            Assert.That(events.Select(e => (e.GetProperty("txnIndex").GetUInt64(), e.GetProperty("eventIndex").GetUInt32())),
                Is.EqualTo(new[] { (2UL, 0u), (4UL, 0u), (4UL, 1u), (9UL, 0u) }));

            var swap = events[1];
            Assert.That(swap.GetProperty("asset0In").GetString(), Is.EqualTo("10"));
            Assert.That(swap.GetProperty("asset1Out").GetString(), Is.EqualTo("1.5"));
            Assert.That(swap.GetProperty("priceNative").GetString(), Is.EqualTo("0.15"));
            Assert.That(swap.TryGetProperty("asset1In", out _), Is.False, "null fields are omitted, not sent as null");
            Assert.That(swap.GetProperty("reserves").GetProperty("asset0").GetString(), Is.EqualTo("5"));
            Assert.That(swap.GetProperty("block").GetProperty("blockTimestamp").GetInt64(), Is.EqualTo(1_760_000_000L));
            Assert.That(events[0].GetProperty("amount0").GetString(), Is.EqualTo("2"));
        }

        [Test]
        public async Task Events_UnknownPoolEventsAreSkipped_NotEmittedBroken()
        {
            _source.Trades.Add(Swap("OK", 10, 1, 0));
            _source.Trades.Add(Swap("UNKNOWNPOOL", 10, 2, 0, appId: 424242));
            _source.Trades.Add(Swap("ZERO", 10, 3, 0, amountOut: 0));
            var events = Events(await Create(c => c.UnknownPoolLoadAttempts = 0).GetEventsJsonAsync(10, 10, default));
            Assert.That(events, Has.Length.EqualTo(1));
            Assert.That(events[0].GetProperty("txnId").GetString(), Is.EqualTo("TOP-OK"));
        }

        [Test]
        public async Task Events_DocumentsWithoutStoredPosition_StillGetUniquePositions()
        {
            // trades indexed before TxnIndex/EventIndex existed
            for (var i = 0; i < 6; i++) _source.Trades.Add(Swap("OLD" + i, 10));
            var events = Events(await Create().GetEventsJsonAsync(10, 10, default));
            var positions = events.Select(e => (e.GetProperty("txnIndex").GetUInt64(), e.GetProperty("eventIndex").GetUInt32())).ToList();
            Assert.That(events, Has.Length.EqualTo(6));
            Assert.That(positions.Distinct().Count(), Is.EqualTo(6));
        }

        [Test]
        public async Task Events_PoolThatCannotBeDescribedRightNow_FailsTheRequest_InsteadOfCachingAnIncompleteAnswer()
        {
            var assetsOk = false;
            _assets.Setup(a => a.GetAssetAsync(12345, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => assetsOk ? Asset(12345, "Late", "LATE", 6, 1000) : null);
            var pool = Pool(901, "TRANSIENT", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            var trade = Swap("T", 10, 1, 0, appId: 901);
            trade.AssetIdOut = 12345;
            _source.Trades.Add(trade);
            _source.Trades.Add(Swap("OK", 10, 2, 0));
            var service = Create(c => c.PoolSnapshotSeconds = 1);

            var failed = await service.GetEventsJsonAsync(10, 10, default);
            Assert.That(failed.Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable), "a retryable 503 - GeckoTerminal keeps its position");

            // once the pool can be described the very same range is answered completely (nothing partial was cached)
            assetsOk = true;
            _time.Advance(TimeSpan.FromSeconds(2));
            var events = Events(await service.GetEventsJsonAsync(10, 10, default));
            Assert.That(events, Has.Length.EqualTo(2));
        }

        [Test]
        public async Task Events_StorageFailure_IsRetryable503_AndNotCached()
        {
            _source.Trades.Add(Swap("S", 10, 1, 0));
            _source.Failure = new InvalidOperationException("Elasticsearch trades query failed");
            var service = Create();

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));

            _source.Failure = null;
            Assert.That(Events(await service.GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(1), "recovers once storage is back, the failure was not remembered");
        }

        [Test]
        public async Task Events_CancelledCaller_DoesNotPoisonTheSharedComputation()
        {
            _source.Trades.Add(Swap("S", 10, 1, 0));
            var service = Create();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await service.GetEventsJsonAsync(10, 10, cts.Token));

            // a later request still gets the right answer, not a stale entry of the cancelled one
            Assert.That(Events(await service.GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task Events_DuplicateDocumentsOfOneTransactionAreEmittedOnce()
        {
            _source.Trades.Add(Swap("DUP", 10, 1, 0));
            _source.Trades.Add(Swap("DUP", 10, 1, 0));
            Assert.That(Events(await Create().GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(1));
        }

        // ----------------------------------------------------------------- pagination / limits

        [Test]
        public async Task Events_FullPagesBisectTheRange_SoNothingIsLost()
        {
            // 25 trades over 10 blocks, but Elasticsearch pages are only 4 documents big
            for (ulong b = 100; b < 110; b++)
            {
                _source.Trades.Add(Swap($"A{b}", b, 1, 0));
                _source.Trades.Add(Swap($"B{b}", b, 2, 0));
                if (b % 2 == 0) _source.Trades.Add(Swap($"C{b}", b, 3, 0));
            }
            _tracker.Latest = new IndexedBlock(5000, 1);
            var service = Create(c => c.ElasticPageSize = 4);

            var events = Events(await service.GetEventsJsonAsync(100, 109, default));

            Assert.That(events, Has.Length.EqualTo(25));
            Assert.That(events.Select(e => e.GetProperty("block").GetProperty("blockNumber").GetUInt64()), Is.Ordered);
            Assert.That(_source.TradeQueries.Count, Is.GreaterThan(1), "the range was split");
        }

        [Test]
        public async Task Events_TooManyEvents_IsBadRequest_NotSilentlyTruncated()
        {
            for (var i = 0; i < 30; i++) _source.Trades.Add(Swap("T" + i, 10, (ulong)i + 1, 0));
            var result = await Create(c => c.MaxEventsPerRequest = 20).GetEventsJsonAsync(10, 10, default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
        }

        // ----------------------------------------------------------------- caching

        [Test]
        public async Task Events_SecondIdenticalRequestIsServedFromCache()
        {
            _source.Trades.Add(Swap("S", 10, 1, 0));
            var service = Create();
            var first = await service.GetEventsJsonAsync(10, 12, default);
            var second = await service.GetEventsJsonAsync(10, 12, default);

            Assert.That(second.Value, Is.EqualTo(first.Value));
            Assert.That(_source.TradeCalls, Is.EqualTo(1));
            Assert.That(_source.LiquidityCalls, Is.EqualTo(1));
        }

        [Test]
        public async Task Events_ConcurrentIdenticalRequestsShareOneQuery()
        {
            _source.Trades.Add(Swap("S", 10, 1, 0));
            var service = Create();
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.GetEventsJsonAsync(10, 12, default))));

            Assert.That(results.All(r => r.Outcome == CoinGeckoOutcome.Ok));
            Assert.That(_source.TradeCalls, Is.LessThanOrEqualTo(2), "single flight + cache: not one query per request");
        }

        [Test]
        public async Task Events_PoolWithADestroyedAsset_IsExcludedForGood_NotStallingTheIntegration()
        {
            var pool = Pool(901, "BROKEN", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            _assets.Setup(a => a.IsDeletedAsync(12345, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var broken = Swap("BROKEN", 10, 1, 0, appId: 901);
            broken.AssetIdOut = 12345;
            _source.Trades.Add(broken);
            _source.Trades.Add(Swap("OK", 10, 2, 0));

            var events = Events(await Create().GetEventsJsonAsync(10, 10, default));

            Assert.That(events, Has.Length.EqualTo(1));
            Assert.That(events[0].GetProperty("txnId").GetString(), Is.EqualTo("TOP-OK"));
        }

        [Test]
        public async Task Events_PoolUnreadableForLong_StaysTransient_NeverCachedAway()
        {
            // the assets of a live pool exist: however long algod misbehaves, its events must wait, not vanish
            var pool = Pool(901, "FLAKY", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            var trade = Swap("T", 10, 1, 0, appId: 901);
            trade.AssetIdOut = 12345;
            _source.Trades.Add(trade);
            var service = Create(c => c.PoolSnapshotSeconds = 1);

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        [Test]
        public async Task PairAndAsset_LookupFailures_AreRetryable503_NotUnhandledErrors()
        {
            var failing = new Mock<IPoolRepository>();
            failing.Setup(p => p.GetAllPoolAddresses()).Throws(new InvalidOperationException("pools unavailable"));
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, failing.Object, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);

            Assert.That((await service.GetPairAsync("1", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await service.GetAssetAsync("0", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        [Test]
        public async Task Asset_RepositoryFailure_IsRetryable503()
        {
            _assets.Setup(a => a.GetAssetAsync(0, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("algod down"));
            Assert.That((await Create().GetAssetAsync("0", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
        }

        [Test]
        public async Task Events_OversizeRange_StopsFanningOut_AndIsRememberedBriefly()
        {
            for (var i = 0; i < 60; i++) _source.Trades.Add(Swap("T" + i, (ulong)(100 + i), 1, 0));
            _tracker.Latest = new IndexedBlock(5000, 1);
            var service = Create(c => { c.MaxEventsPerRequest = 10; c.ElasticPageSize = 5; });

            Assert.That((await service.GetEventsJsonAsync(100, 159, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
            var queriesAfterFirst = _source.TradeCalls;
            Assert.That((await service.GetEventsJsonAsync(100, 159, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest));
            Assert.That(_source.TradeCalls, Is.EqualTo(queriesAfterFirst), "the oversize answer is remembered - no second fan-out against storage");
        }

        [Test]
        public async Task Events_RecentEventOfAPoolNobodyKnowsYet_IsHeldBack_NotSkippedAndCachedAsFinal()
        {
            // a pool created moments ago may not be registered in the pool cache yet
            var fresh = Swap("FRESH", 10, 1, 0, appId: 424242);
            fresh.Timestamp = _time.GetUtcNow();
            _source.Trades.Add(fresh);
            var service = Create();

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));

            // the same pool registered a moment later -> the very same range is complete
            await _pools.StorePoolAsync(Pool(424242, "FRESHPOOL", 0, 31566704));
            _time.Advance(TimeSpan.FromSeconds(6));
            Assert.That(Events(await service.GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task Events_RecentEventOfAnExcludedScamPool_IsSkippedForGood_NotHeldBack()
        {
            var scam = Pool(666, "SCAMMY", 0, 31566704);
            scam.ScamRating = 95;
            await _pools.StorePoolAsync(scam);
            var trade = Swap("SCAM", 10, 1, 0, appId: 666);
            trade.Timestamp = _time.GetUtcNow();
            _source.Trades.Add(trade);
            Assert.That(Events(await Create().GetEventsJsonAsync(10, 10, default)), Is.Empty);
        }

        [Test]
        public async Task Events_RecentEventOfARelabelledScamPool_DoesNotStallTheFeed()
        {
            // the pool was relabelled DEXProtocol.Scam after its swaps were stored as Biatec: known to the cache -> skipped, no 503
            await _pools.StorePoolAsync(Pool(667, "RELABELLED", 0, 31566704, DEXProtocol.Scam));
            var trade = Swap("SCAM2", 10, 1, 0, appId: 667);
            trade.Timestamp = _time.GetUtcNow();
            _source.Trades.Add(trade);
            _source.Trades.Add(Swap("OK", 10, 2, 0));
            var events = Events(await Create().GetEventsJsonAsync(10, 10, default));
            Assert.That(events, Has.Length.EqualTo(1));
        }

        [Test]
        public async Task Events_EachPoolIsResolvedOncePerRequest()
        {
            for (var i = 0; i < 50; i++) _source.Trades.Add(Swap("T" + i, 10, (ulong)i + 1, 0));
            var (counting, calls) = CountingPools();
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, counting.Object, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);

            Assert.That(Events(await service.GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(50));
            Assert.That(calls.Value, Is.EqualTo(1), "one snapshot load, not one per event");
        }

        [Test]
        public async Task Events_EmptyPoolCache_IsTransient_NotCachedAsNoEvents()
        {
            // the pod's pool cache failed to initialise / is still warming: every pool looks unknown
            _pools = new MockPoolRepository { PoolLoadSucceeded = false };
            _source.Trades.Add(Swap("OLD", 10, 1, 0)); // older than UnknownPoolGraceSeconds
            var service = Create(c => c.PoolSnapshotSeconds = 1);

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await service.GetPairAsync(PoolAppId.ToString(), default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That((await service.GetAssetAsync("0", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable), "an asset is not 'unknown' while the pool cache is empty");

            await _pools.StorePoolAsync(Pool(PoolAppId, "POOLADDR", 0, 31566704));
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.That(Events(await service.GetEventsJsonAsync(10, 10, default)), Has.Length.EqualTo(1), "nothing partial was cached");
        }

        [Test]
        public async Task LoadedButEmptyPoolCache_IsAnHonest404AndEmptyEvents_NotATransient503()
        {
            // a network the DEX is not on yet (Voi): the pool cache loaded fine and holds no pool at all. Answering 503 for ever
            // failed the production smoke test and would stall a consumer that polls it - nothing is wrong, there is just nothing here.
            _pools = new MockPoolRepository { PoolLoadSucceeded = true };
            var service = Create();

            Assert.That((await service.GetPairAsync("1", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            Assert.That((await service.GetAssetAsync("0", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            var events = await service.GetEventsJsonAsync(10, 10, default);
            Assert.That(events.Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            Assert.That(Events(events), Is.Empty);
        }

        [Test]
        public async Task NoPoolOfAPublishedProtocol_IsAnHonest404_NotATransient503()
        {
            // a network with Pact/Tiny pools only: the pool cache is fine, there just is nothing to publish
            _pools = new MockPoolRepository();
            await _pools.StorePoolAsync(Pool(777, "PACTADDR", 0, 31566704, DEXProtocol.Pact));
            var service = Create();
            Assert.That((await service.GetPairAsync("777", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
            Assert.That((await service.GetPairAsync("1", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));
        }

        [Test]
        public async Task Events_BisectionFanOutIsBounded()
        {
            for (ulong b = 100; b < 164; b++) _source.Trades.Add(Swap("T" + b, b, 1, 0));
            _source.QueryDelay = TimeSpan.FromMilliseconds(5);
            _tracker.Latest = new IndexedBlock(5000, 1);
            var service = Create(c => { c.ElasticPageSize = 1; c.MaxParallelQueriesPerRange = 2; });

            Assert.That(Events(await service.GetEventsJsonAsync(100, 163, default)), Has.Length.EqualTo(64));
            Assert.That(_source.MaxInFlight, Is.LessThanOrEqualTo(2));
            Assert.That(_source.TradeCalls, Is.GreaterThan(64), "the range really was bisected");
        }

        [Test]
        public async Task ForcedRefreshForAnUnknownId_DoesNotRetryAlgoLookups()
        {
            // one published pool whose decimals cannot be resolved -> the asset repository is asked on the regular refresh only
            var pool = Pool(901, "UNRESOLVED", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            var service = Create(c => c.PoolSnapshotSeconds = 3600);
            Assert.That((await service.GetPairAsync("901", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            var lookups = _assets.Invocations.Count(i => i.Method.Name == nameof(IAssetRepository.GetAssetAsync) && (ulong)i.Arguments[0] == 12345);
            Assert.That(lookups, Is.GreaterThan(0));

            _time.Advance(TimeSpan.FromSeconds(6)); // past MissingPoolRefreshInterval
            Assert.That((await service.GetPairAsync("777777", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound));

            Assert.That(_assets.Invocations.Count(i => i.Method.Name == nameof(IAssetRepository.GetAssetAsync) && (ulong)i.Arguments[0] == 12345), Is.EqualTo(lookups));
        }

        [Test]
        public async Task WarmUp_BuildsTheSnapshot_SoTheFirstRequestDoesNotLoadPools()
        {
            var (counting, calls) = CountingPools();
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, counting.Object, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);

            await service.WarmUpAsync(default);
            Assert.That(calls.Value, Is.EqualTo(1));
            Assert.That((await service.GetPairAsync(PoolAppId.ToString(), default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
            Assert.That(calls.Value, Is.EqualTo(1));
        }

        [Test]
        public async Task ExpiredSnapshot_IsServedStaleWhileOneRebuildRuns()
        {
            using var gate = new ManualResetEventSlim(initialState: true);
            var (repo, loads) = CountingPools(gate);
            _config.CoinGecko.PoolSnapshotSeconds = 1;
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, repo.Object, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);
            await service.WarmUpAsync(default);
            gate.Reset(); // the next rebuild blocks inside the pool cache walk
            _time.Advance(TimeSpan.FromSeconds(2));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetPairAsync(PoolAppId.ToString(), default)));
            watch.Stop();

            Assert.That(answers.All(a => a.Outcome == CoinGeckoOutcome.Ok));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500), "answered from the previous snapshot, not after the stuck rebuild");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (loads.Value < 2 && DateTime.UtcNow < deadline) await Task.Delay(10); // the background rebuild is scheduled, not awaited
            Assert.That(loads.Value, Is.EqualTo(2), "exactly one rebuild was started");
            gate.Set();
        }

        [Test]
        public async Task ForcedRebuild_WhenTheRegularRefreshIsDue_RetriesUnresolvedPools()
        {
            // under steady probing every rebuild is a "forced" one; the asset lookups of unresolved pools must still happen on time
            var assetsOk = false;
            _assets.Setup(a => a.GetAssetAsync(12345, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => assetsOk ? Asset(12345, "Late", "LATE", 6, 1000) : null);
            var pool = Pool(901, "LATE", 0, 12345);
            pool.AssetADecimals = null;
            pool.AssetBDecimals = null;
            await _pools.StorePoolAsync(pool);
            var service = Create(c => c.PoolSnapshotSeconds = 1);
            Assert.That((await service.GetPairAsync("901", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));

            assetsOk = true;
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.That((await service.GetPairAsync("777777", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.NotFound), "an unknown id forces the rebuild");
            Assert.That((await service.GetPairAsync("901", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok));
        }

        [Test]
        public async Task Events_BlockDenserThanOneQuery_IsADeterministic400()
        {
            for (var i = 0; i < 10_000; i++) _source.Trades.Add(Swap("T" + i, 10, (ulong)i + 1, 0));
            var result = await Create(c => c.MaxEventsPerRequest = 100_000).GetEventsJsonAsync(10, 10, default);
            Assert.That(result.Outcome, Is.EqualTo(CoinGeckoOutcome.BadRequest), "retrying can never help - a 503 would stall the consumer for ever");
        }

        private sealed class RegisteringPools : IPoolRepository
        {
            private readonly List<PoolModel> _pools = new();
            public PoolModel? OnChain { get; set; }
            public int Loads;

            public RegisteringPools(PoolModel initial) => _pools.Add(initial);

            public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<PoolModel?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken) => Task.FromResult(GetCachedPool(poolAddress));
            public PoolModel? GetCachedPool(string poolAddress) => _pools.FirstOrDefault(p => p.PoolAddress == poolAddress);
            public Task<bool> StorePoolAsync(PoolModel pool, bool updateAggregated = true, CancellationToken? cancellationToken = null) { _pools.Add(pool); return Task.FromResult(true); }
            public Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<List<PoolModel>> GetPoolsAsync(ulong? assetIdA, ulong? assetIdB, string? address, DEXProtocol? protocol = null, int size = 100, PoolOrderBy? orderBy = null, SortDirection direction = SortDirection.Desc, CancellationToken cancellationToken = default) => Task.FromResult(_pools.ToList());
            public Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(_pools.Count);
            public Task UpdateAggregatedPool(ulong aId, ulong bId, CancellationToken cancellationToken) => Task.CompletedTask;
            public bool PoolLoadSucceeded { get; set; } = true;
            public IEnumerable<string> GetAllPoolAddresses() => _pools.Select(p => p.PoolAddress).ToList();

            public AVMTradeReporter.Processors.Pool.IPoolProcessor? GetPoolProcessor(DEXProtocol protocol)
            {
                var processor = new Mock<AVMTradeReporter.Processors.Pool.IPoolProcessor>();
                processor.Setup(p => p.LoadPoolAsync(It.IsAny<string>(), It.IsAny<ulong>())).Returns(() => { Loads++; return Task.FromResult(OnChain!); });
                return processor.Object;
            }
        }

        [Test]
        public async Task Events_OldEventOfAPoolTheCacheMissed_RegistersThePoolFromChain()
        {
            var pools = new RegisteringPools(Pool(PoolAppId, "POOLADDR", 0, 31566704)) { OnChain = Pool(424242, "MISSED", 0, 31566704) };
            var trade = Swap("MISSED", 10, 1, 0, appId: 424242);
            trade.PoolAddress = "MISSED";
            _source.Trades.Add(trade); // older than the grace period
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, pools, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);

            var events = Events(await service.GetEventsJsonAsync(10, 10, default));

            Assert.That(events, Has.Length.EqualTo(1));
            Assert.That(events[0].GetProperty("pairId").GetString(), Is.EqualTo("424242"));
            Assert.That(pools.Loads, Is.EqualTo(1));
            Assert.That((await service.GetPairAsync("424242", default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Ok), "registered for good");
        }

        [Test]
        public async Task Events_PoolThatCannotBeLoadedFromChain_IsRetryable503_UntilTheAttemptsAreUsedUp()
        {
            var pools = new RegisteringPools(Pool(PoolAppId, "POOLADDR", 0, 31566704)) { OnChain = null };
            _source.Trades.Add(Swap("GONE", 10, 1, 0, appId: 424242));
            _source.Trades.Add(Swap("OK", 10, 2, 0));
            _config.CoinGecko.UnknownPoolLoadAttempts = 1;
            var service = new CoinGeckoService(Options.Create(_config), _tracker, _source, pools, _assets.Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance, _time);

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable), "one load attempt, failed");
            var events = Events(await service.GetEventsJsonAsync(10, 10, default));
            Assert.That(events.Select(e => e.GetProperty("txnId").GetString()), Is.EqualTo(new[] { "TOP-OK" }), "attempts used up: the pool's events are skipped, the rest is served");
        }

        [Test]
        public async Task Events_TransientFailure_IsRememberedBriefly_SoRetriesDoNotHitStorage()
        {
            var fresh = Swap("FRESH", 10, 1, 0, appId: 424242);
            fresh.Timestamp = _time.GetUtcNow();
            _source.Trades.Add(fresh);
            var service = Create(c => c.TransientMemoSeconds = 30);

            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            var queries = _source.TradeCalls;
            Assert.That((await service.GetEventsJsonAsync(10, 10, default)).Outcome, Is.EqualTo(CoinGeckoOutcome.Unavailable));
            Assert.That(_source.TradeCalls, Is.EqualTo(queries), "the 2-second retry is answered from the memo");
        }

        [Test]
        public async Task Events_OutputIsPlainJsonWithEventsArray()
        {
            var json = System.Text.Encoding.UTF8.GetString((await Create().GetEventsJsonAsync(1, 1, default)).Value!);
            Assert.That(json, Is.EqualTo("{\"events\":[]}"));
        }
    }
}
