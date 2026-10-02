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
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Conformance
{
    /// <summary>Fakes and builders shared by the offline conformance and performance suites.</summary>
    public static class CoinGeckoTestKit
    {
        public static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
        public const ulong Usdc = 31566704;

        public sealed class FakeTracker : IIndexedBlockTracker
        {
            public IndexedBlock? Latest { get; set; } = new IndexedBlock(10_000_000, 1_760_000_100);
            public IndexedBlock? CompletedThrough => Latest;
            public void Seed(ulong completedRound, long? unixTimestamp, bool verified = true) => Latest = new IndexedBlock(completedRound, unixTimestamp ?? 0);
            public void MarkCompleted(ulong round, long? unixTimestamp) => Latest = new IndexedBlock(round, unixTimestamp ?? 0);
            public Task<IndexedBlock?> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(Latest);
        }

        public sealed class FakeSource : ICoinGeckoEventSource
        {
            private readonly List<Trade> _trades = new();
            private readonly List<Liquidity> _liquidity = new();
            public bool IsAvailable { get; set; } = true;
            public TimeSpan QueryDelay { get; set; }
            public int Queries;

            public void Add(IEnumerable<Trade> trades) => _trades.AddRange(trades);
            public void Add(IEnumerable<Liquidity> liquidity) => _liquidity.AddRange(liquidity);

            public async Task<IReadOnlyList<Trade>> GetTradesAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Queries);
                if (QueryDelay > TimeSpan.Zero) await Task.Delay(QueryDelay, cancellationToken);
                return _trades.Where(t => t.BlockId >= lo && t.BlockId <= hi).OrderBy(t => t.BlockId).Take(size).ToList();
            }

            public async Task<IReadOnlyList<Liquidity>> GetLiquidityAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Queries);
                if (QueryDelay > TimeSpan.Zero) await Task.Delay(QueryDelay, cancellationToken);
                return _liquidity.Where(t => t.BlockId >= lo && t.BlockId <= hi).OrderBy(t => t.BlockId).Take(size).ToList();
            }
        }

        public static PoolModel MakePool(ulong appId, ulong assetA = 0, ulong assetB = Usdc, bool decimals = true) => new()
        {
            PoolAddress = "POOL" + appId,
            PoolAppId = appId,
            AssetIdA = assetA,
            AssetIdB = assetB,
            AssetADecimals = decimals ? 6UL : null,
            AssetBDecimals = decimals ? 6UL : null,
            Protocol = DEXProtocol.Biatec,
            LPFee = 0.003m,
            Timestamp = Time,
        };

        public static Trade Swap(string txId, ulong block, ulong txn, ulong appId, bool zeroToOne = true) => new()
        {
            AssetIdIn = zeroToOne ? 0 : Usdc,
            AssetIdOut = zeroToOne ? Usdc : 0,
            AssetAmountIn = zeroToOne ? 10_000_000UL : 1_500_000UL,
            AssetAmountOut = zeroToOne ? 1_500_000UL : 10_000_000UL,
            TxId = txId,
            TopTxId = "TOP-" + txId,
            BlockId = block,
            Timestamp = Time,
            Trader = "TRADER",
            PoolAddress = "POOL" + appId,
            PoolAppId = appId,
            A = 5_000_000_000,
            B = 7_000_000_000,
            TradeState = TxState.Confirmed,
            Protocol = DEXProtocol.Biatec,
            TxnIndex = txn,
            EventIndex = 0,
        };

        public static Liquidity Deposit(string txId, ulong block, ulong txn, ulong appId, bool exit = false) => new()
        {
            Direction = exit ? LiquidityDirection.WithdrawLiquidity : LiquidityDirection.DepositLiquidity,
            AssetIdA = 0,
            AssetIdB = Usdc,
            AssetAmountA = 2_000_000,
            AssetAmountB = 300_000,
            A = 1_000_000_000,
            B = 2_000_000_000,
            TxId = txId,
            TopTxId = "TOP-" + txId,
            BlockId = block,
            Timestamp = Time,
            LiquidityProvider = "LP",
            PoolAddress = "POOL" + appId,
            PoolAppId = appId,
            TxnIndex = txn,
            EventIndex = 0,
            Protocol = DEXProtocol.Biatec,
            TxState = TxState.Confirmed,
        };

        public static Mock<IAssetRepository> Assets()
        {
            var assets = new Mock<IAssetRepository>();
            assets.Setup(a => a.GetAssetAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>())).ReturnsAsync((ulong id, CancellationToken _) => new BiatecAsset
            {
                Index = id,
                Type = AssetType.ASA,
                Params = new Algorand.Algod.Model.AssetParams { Name = "Asset " + id, UnitName = "A" + id, Decimals = 6, Total = 1_000_000_000_000, Url = "https://asset.test" },
            });
            assets.Setup(a => a.IsDeletedAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            return assets;
        }

        public static CoinGeckoService Service(ICoinGeckoEventSource source, IPoolRepository pools, IIndexedBlockTracker? tracker = null, Action<CoinGeckoConfiguration>? tweak = null, IAssetRepository? assets = null)
        {
            var config = new AppConfiguration();
            config.Redis.Enabled = false;
            config.CoinGecko.TransientMemoSeconds = 0;
            tweak?.Invoke(config.CoinGecko);
            return new CoinGeckoService(Options.Create(config), tracker ?? new FakeTracker(), source, pools, assets ?? Assets().Object, new ServiceCollection().BuildServiceProvider(), NullLogger<CoinGeckoService>.Instance);
        }
    }
}
