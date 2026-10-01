using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services.CoinGecko;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class CoinGeckoMapperTests
    {
        private const ulong Algo = 0;
        private const ulong Usdc = 31566704;

        // ALGO (6 decimals) / USDC (6 decimals) Biatec pool with a 0.3 % LP fee, reserves in the 1e9 contract scale
        private static readonly PairInfo BiatecPair = new(3136517663, DEXProtocol.Biatec, Algo, Usdc, 6, 6, 0.003m);
        private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

        private static Trade Swap(ulong idIn, ulong idOut, ulong amountIn, ulong amountOut) => new()
        {
            AssetIdIn = idIn,
            AssetIdOut = idOut,
            AssetAmountIn = amountIn,
            AssetAmountOut = amountOut,
            TxId = "INNERTX",
            TopTxId = "TOPTX",
            BlockId = 55991837,
            Timestamp = Time,
            Trader = "TRADER",
            PoolAppId = 3136517663,
            A = 620_652_196_491_000,
            B = 143_764_374_536_000,
            TradeState = TxState.Confirmed,
        };

        [TestCase(0UL, "0")]
        [TestCase(1UL, "0.000001")]
        [TestCase(1_500_000UL, "1.5")]
        [TestCase(123_456_789UL, "123.456789")]
        [TestCase(18_446_744_073_709_551_615UL, "18446744073709.551615")]
        public void Decimalize_AndFormat_6Decimals(ulong raw, string expected)
        {
            Assert.That(CoinGeckoMapper.Format(CoinGeckoMapper.Decimalize(raw, 6)!.Value), Is.EqualTo(expected));
        }

        [Test]
        public void Decimalize_ZeroDecimalsAndMaxAsaDecimals()
        {
            Assert.That(CoinGeckoMapper.Format(CoinGeckoMapper.Decimalize(42, 0)!.Value), Is.EqualTo("42"));
            Assert.That(CoinGeckoMapper.Format(CoinGeckoMapper.Decimalize(1, 19)!.Value), Is.EqualTo("0.0000000000000000001"));
            Assert.That(CoinGeckoMapper.Decimalize(1, 29), Is.Null, "decimal cannot express more than 28 digits - must not throw");
            Assert.That(CoinGeckoMapper.Decimalize(1, -1), Is.Null);
        }

        [Test]
        public void Format_NeverUsesExponentNorTrailingZeros()
        {
            Assert.That(CoinGeckoMapper.Format(0.0000001m), Is.EqualTo("0.0000001"));
            Assert.That(CoinGeckoMapper.Format(100m), Is.EqualTo("100"));
            Assert.That(CoinGeckoMapper.Format(1.500m), Is.EqualTo("1.5"));
            Assert.That(CoinGeckoMapper.Format(-0m), Is.EqualTo("0"));
        }

        [Test]
        public void Swap_Asset0In_MapsAmountsPriceReservesAndFee()
        {
            // 10 ALGO in, 1.5 USDC out -> 1 ALGO = 0.15 USDC
            var swap = CoinGeckoMapper.TryMapSwap(Swap(Algo, Usdc, 10_000_000, 1_500_000), BiatecPair, new EventPositionPair(7, 2));

            Assert.That(swap, Is.Not.Null);
            Assert.That(swap!.EventType, Is.EqualTo("swap"));
            Assert.That(swap.Asset0In, Is.EqualTo("10"));
            Assert.That(swap.Asset1Out, Is.EqualTo("1.5"));
            Assert.That(swap.Asset1In, Is.Null);
            Assert.That(swap.Asset0Out, Is.Null);
            Assert.That(swap.PriceNative, Is.EqualTo("0.15"));
            Assert.That(swap.Metadata!.Fees0In, Is.EqualTo("0.03"));
            Assert.That(swap.Metadata.Fees1In, Is.Null);
            Assert.That(swap.TxnId, Is.EqualTo("TOPTX"), "the explorer-visible top level transaction");
            Assert.That(swap.TxnIndex, Is.EqualTo(7UL));
            Assert.That(swap.EventIndex, Is.EqualTo(2u));
            Assert.That(swap.Maker, Is.EqualTo("TRADER"));
            Assert.That(swap.PairId, Is.EqualTo("3136517663"));
            Assert.That(swap.Block.BlockNumber, Is.EqualTo(55991837UL));
            Assert.That(swap.Block.BlockTimestamp, Is.EqualTo(1_760_000_000L), "unix seconds");
            // Biatec reserves are 1e9 scaled irrespective of the asset decimals
            Assert.That(swap.Reserves.Asset0, Is.EqualTo("620652.196491"));
            Assert.That(swap.Reserves.Asset1, Is.EqualTo("143764.374536"));
        }

        [Test]
        public void Swap_Asset1In_MapsReverseDirectionWithSamePriceDefinition()
        {
            // 3 USDC in, 20 ALGO out -> still "price of asset0 (ALGO) quoted in asset1 (USDC)" = 0.15
            var swap = CoinGeckoMapper.TryMapSwap(Swap(Usdc, Algo, 3_000_000, 20_000_000), BiatecPair, new EventPositionPair(1, 0));

            Assert.That(swap, Is.Not.Null);
            Assert.That(swap!.Asset1In, Is.EqualTo("3"));
            Assert.That(swap.Asset0Out, Is.EqualTo("20"));
            Assert.That(swap.Asset0In, Is.Null);
            Assert.That(swap.Asset1Out, Is.Null);
            Assert.That(swap.PriceNative, Is.EqualTo("0.15"));
            Assert.That(swap.Metadata!.Fees1In, Is.EqualTo("0.009"));
        }

        [Test]
        public void Swap_DifferentDecimals_AreDecimalizedPerAsset()
        {
            // asset0 has 8 decimals, asset1 has 2 decimals
            var pair = new PairInfo(5, DEXProtocol.Biatec, 100, 200, 8, 2, null);
            var trade = Swap(100, 200, 200_000_000, 500); // 2.0 asset0 for 5.00 asset1
            var swap = CoinGeckoMapper.TryMapSwap(trade, pair, new EventPositionPair(1, 0));

            Assert.That(swap!.Asset0In, Is.EqualTo("2"));
            Assert.That(swap.Asset1Out, Is.EqualTo("5"));
            Assert.That(swap.PriceNative, Is.EqualTo("2.5"));
            Assert.That(swap.Metadata, Is.Null, "no LP fee known -> no fee metadata");
        }

        [Test]
        public void Swap_NonBiatecProtocol_ReservesUseAssetDecimals()
        {
            var pair = new PairInfo(9, DEXProtocol.Pact, Algo, Usdc, 6, 6, null);
            var trade = Swap(Algo, Usdc, 1_000_000, 150_000);
            trade.A = 2_000_000;
            trade.B = 300_000;
            var swap = CoinGeckoMapper.TryMapSwap(trade, pair, new EventPositionPair(1, 0));

            Assert.That(swap!.Reserves.Asset0, Is.EqualTo("2"));
            Assert.That(swap.Reserves.Asset1, Is.EqualTo("0.3"));
        }

        [Test]
        public void Swap_FallsBackToInnerTxIdWhenNoTopLevelId()
        {
            var trade = Swap(Algo, Usdc, 1_000_000, 150_000);
            trade.TopTxId = string.Empty;
            Assert.That(CoinGeckoMapper.TryMapSwap(trade, BiatecPair, new EventPositionPair(1, 0))!.TxnId, Is.EqualTo("INNERTX"));
        }

        [Test]
        public void Swap_UnmappableEventsAreSkippedNotGuessed()
        {
            var position = new EventPositionPair(1, 0);
            Assert.That(CoinGeckoMapper.TryMapSwap(Swap(Algo, Usdc, 0, 1), BiatecPair, position), Is.Null, "zero in");
            Assert.That(CoinGeckoMapper.TryMapSwap(Swap(Algo, Usdc, 1, 0), BiatecPair, position), Is.Null, "zero out");
            Assert.That(CoinGeckoMapper.TryMapSwap(Swap(Algo, 999, 1, 1), BiatecPair, position), Is.Null, "asset not in the pair");
            Assert.That(CoinGeckoMapper.TryMapSwap(Swap(Algo, Algo, 1, 1), BiatecPair, position), Is.Null, "same asset on both sides");

            var noTime = Swap(Algo, Usdc, 1, 1);
            noTime.Timestamp = null;
            Assert.That(CoinGeckoMapper.TryMapSwap(noTime, BiatecPair, position), Is.Null);

            var noMaker = Swap(Algo, Usdc, 1_000_000, 1_000_000);
            noMaker.Trader = string.Empty;
            Assert.That(CoinGeckoMapper.TryMapSwap(noMaker, BiatecPair, position), Is.Null);
        }

        [Test]
        public void Swap_WithBothReservesZero_IsSkipped_AndSoIsAnExitWithUnknownReserves()
        {
            // reserves 0/0 after a swap = the processor could not read the pool state, not an empty pool
            var swap = Swap(Algo, Usdc, 1_000_000, 150_000);
            swap.A = 0;
            swap.B = 0;
            Assert.That(CoinGeckoMapper.TryMapSwap(swap, BiatecPair, new EventPositionPair(1, 0)), Is.Null);
            swap.A = 5_000_000_000; // a lone 0 is a real state: a range pool pushed to its bound holds nothing of one asset
            Assert.That(CoinGeckoMapper.TryMapSwap(swap, BiatecPair, new EventPositionPair(1, 0))!.Reserves.Asset1, Is.EqualTo("0"));

            // 0/0 after a withdrawal may be a full drain or a partial exit whose deltas were not parsed - not reportable
            var exit = new Liquidity
            {
                Direction = LiquidityDirection.WithdrawLiquidity,
                AssetIdA = Algo,
                AssetIdB = Usdc,
                AssetAmountA = 1_000_000,
                AssetAmountB = 150_000,
                TxId = "L",
                Timestamp = Time,
                LiquidityProvider = "LP",
            };
            Assert.That(CoinGeckoMapper.TryMapLiquidity(exit, BiatecPair, new EventPositionPair(1, 0)), Is.Null);
        }

        [Test]
        public void Swap_PriceBeyondDecimalRange_IsSkipped_NotThrown()
        {
            // 1 base unit of a 19-decimal asset0 for 1e10 units of a 0-decimal asset1: price 1e29 > decimal.MaxValue
            var pair = new PairInfo(5, DEXProtocol.Biatec, 100, 200, 19, 0, null);
            Assert.That(CoinGeckoMapper.TryMapSwap(Swap(100, 200, 1, 10_000_000_000), pair, new EventPositionPair(1, 0)), Is.Null);
        }

        [Test]
        public void Swap_PriceNeverZero_EvenForDustAgainstHugeAmount()
        {
            // 1 base unit of asset1 (6 decimals) for a huge amount of asset0 with 19 decimals -> price below decimal precision
            var pair = new PairInfo(5, DEXProtocol.Biatec, 100, 200, 19, 6, null);
            var swap = CoinGeckoMapper.TryMapSwap(Swap(100, 200, ulong.MaxValue, 1), pair, new EventPositionPair(1, 0));
            if (swap != null) Assert.That(decimal.Parse(swap.PriceNative, System.Globalization.CultureInfo.InvariantCulture), Is.GreaterThan(0m));
        }

        [Test]
        public void Liquidity_DepositIsJoin_WithdrawIsExit()
        {
            Liquidity Make(LiquidityDirection direction) => new()
            {
                Direction = direction,
                AssetIdA = Algo,
                AssetIdB = Usdc,
                AssetAmountA = 5_000_000,
                AssetAmountB = 750_000,
                A = 1_000_000_000,
                B = 2_500_000_000,
                TxId = "LIQ",
                TopTxId = "LIQTOP",
                BlockId = 100,
                Timestamp = Time,
                LiquidityProvider = "LP",
                PoolAppId = 3136517663,
            };

            var join = CoinGeckoMapper.TryMapLiquidity(Make(LiquidityDirection.DepositLiquidity), BiatecPair, new EventPositionPair(4, 1));
            var exit = CoinGeckoMapper.TryMapLiquidity(Make(LiquidityDirection.WithdrawLiquidity), BiatecPair, new EventPositionPair(4, 1));

            Assert.That(join!.EventType, Is.EqualTo("join"));
            Assert.That(exit!.EventType, Is.EqualTo("exit"));
            Assert.That(join.Amount0, Is.EqualTo("5"));
            Assert.That(join.Amount1, Is.EqualTo("0.75"));
            Assert.That(join.Reserves.Asset0, Is.EqualTo("1"));
            Assert.That(join.Reserves.Asset1, Is.EqualTo("2.5"));
            Assert.That(join.Maker, Is.EqualTo("LP"));
            Assert.That(join.TxnId, Is.EqualTo("LIQTOP"));
            Assert.That(join.TxnIndex, Is.EqualTo(4UL));
            Assert.That(join.EventIndex, Is.EqualTo(1u));
        }

        [Test]
        public void Liquidity_AssetsStoredInOppositeOrder_AreFlippedToPairOrder()
        {
            var liquidity = new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                AssetIdA = Usdc,
                AssetIdB = Algo,
                AssetAmountA = 750_000,
                AssetAmountB = 5_000_000,
                A = 1_000_000_000,
                B = 2_000_000_000,
                TxId = "L",
                BlockId = 1,
                Timestamp = Time,
                LiquidityProvider = "LP",
            };
            var join = CoinGeckoMapper.TryMapLiquidity(liquidity, BiatecPair, new EventPositionPair(1, 0));
            Assert.That(join!.Amount0, Is.EqualTo("5"));
            Assert.That(join.Amount1, Is.EqualTo("0.75"));
        }

        [Test]
        public void Liquidity_UnmappableIsSkipped()
        {
            var empty = new Liquidity { AssetIdA = Algo, AssetIdB = Usdc, TxId = "L", Timestamp = Time, LiquidityProvider = "LP" };
            Assert.That(CoinGeckoMapper.TryMapLiquidity(empty, BiatecPair, new EventPositionPair(1, 0)), Is.Null, "no amounts");

            var wrongAssets = new Liquidity { AssetIdA = 1, AssetIdB = 2, AssetAmountA = 1, TxId = "L", Timestamp = Time, LiquidityProvider = "LP" };
            Assert.That(CoinGeckoMapper.TryMapLiquidity(wrongAssets, BiatecPair, new EventPositionPair(1, 0)), Is.Null, "assets not in the pair");
        }

        [Test]
        public void Liquidity_WithAnUnknownReserveSide_IsSkipped_BecauseDeltaZeroMeansUnchangedNotEmpty()
        {
            // a single-sided CLAMM deposit changes only one reserve: the other one is absent from the state delta and stored as 0
            Liquidity Make(ulong a, ulong b, LiquidityDirection direction) => new()
            {
                Direction = direction,
                AssetIdA = Algo,
                AssetIdB = Usdc,
                AssetAmountA = 1_000_000,
                AssetAmountB = 0,
                A = a,
                B = b,
                TxId = "L",
                Timestamp = Time,
                LiquidityProvider = "LP",
            };
            var position = new EventPositionPair(1, 0);

            Assert.That(CoinGeckoMapper.TryMapLiquidity(Make(5_000_000_000, 0, LiquidityDirection.DepositLiquidity), BiatecPair, position), Is.Null);
            Assert.That(CoinGeckoMapper.TryMapLiquidity(Make(0, 5_000_000_000, LiquidityDirection.WithdrawLiquidity), BiatecPair, position), Is.Null);
            Assert.That(CoinGeckoMapper.TryMapLiquidity(Make(0, 0, LiquidityDirection.DepositLiquidity), BiatecPair, position), Is.Null, "a deposit cannot leave an empty pool");

            var singleSided = CoinGeckoMapper.TryMapLiquidity(Make(5_000_000_000, 7_000_000_000, LiquidityDirection.DepositLiquidity), BiatecPair, position);
            Assert.That(singleSided!.Amount0, Is.EqualTo("1"));
            Assert.That(singleSided.Amount1, Is.EqualTo("0"));
        }
    }
}
