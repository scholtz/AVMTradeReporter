using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors;

namespace AVMTradeReporterTests.Model
{
    /// <summary>
    /// Price decoding, virtual amounts and incremental reserve tracking for tick based CLAMM pools (Pact CLAMM).
    /// </summary>
    public class TickBasedClammPoolTests
    {
        private static AVMTradeReporter.Models.Data.Pool MakePool(ulong a, ulong b, decimal price)
        {
            return new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "GMHCJSYIJVRPGX2F6RJNK7J4TBJWYRU2ZBXRUWHHRZPORTE65IW6PT5HCE",
                PoolAppId = 763274703,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.TickBasedCLAMM,
                AssetIdA = 0,
                AssetADecimals = 6,
                AssetIdB = 10458941,
                AssetBDecimals = 6,
                A = a,
                B = b,
                L = 0,
                CurrentPrice = price,
                CurrentTick = 1000361,
                TickSpacing = 60,
            };
        }

        [Test]
        public void SqrtPriceX64DecodesToPrice()
        {
            // value from the testnet pool 763274703 after the 5 ALGO -> 0.572191 USDC swap
            var price = PactClammHelper.SqrtPriceX64ToPrice(6237834869436999446UL, 6, 6);
            Assert.That(price, Is.EqualTo(0.1143479m).Within(0.0000001m));
            // 5000000 in -> 572191 out => ~0.1144 including fee and slippage
            Assert.That(price, Is.EqualTo(572191m / 5000000m).Within(0.001m));
        }

        [Test]
        public void SqrtPriceX64AdjustsForDecimals()
        {
            var price6to6 = PactClammHelper.SqrtPriceX64ToPrice(6237834869436999446UL, 6, 6);
            var price6to8 = PactClammHelper.SqrtPriceX64ToPrice(6237834869436999446UL, 6, 8);
            Assert.That(price6to8, Is.EqualTo(price6to6 / 100).Within(0.0000001m));
            Assert.That(PactClammHelper.SqrtPriceX64ToPrice(0, 6, 6), Is.EqualTo(0));
        }

        [Test]
        public void VirtualAmountsFollowCurrentPrice()
        {
            var pool = MakePool(5_000_000, 572_191, 0.1143479m);

            var price = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(price, Is.EqualTo(0.1143479m).Within(0.000001m));
            // product of the virtual amounts equals the product of the real reserves
            Assert.That(pool.VirtualAmountA * pool.VirtualAmountB, Is.EqualTo(pool.RealAmountA * pool.RealAmountB).Within(0.0001m));
            Assert.That(pool.RealAmountA, Is.EqualTo(5m));
            Assert.That(pool.RealAmountB, Is.EqualTo(0.572191m));
        }

        [Test]
        public void VirtualAmountsFallBackToRealWithoutPrice()
        {
            var pool = MakePool(5_000_000, 572_191, 0);
            pool.CurrentPrice = null;
            Assert.That(pool.VirtualAmountA, Is.EqualTo(pool.RealAmountA));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(pool.RealAmountB));
        }

        [Test]
        public void ReverseInvertsPrice()
        {
            var pool = MakePool(5_000_000, 572_191, 0.1143479m);
            var reversed = pool.Reverse();
            Assert.That(reversed.CurrentPrice, Is.EqualTo(1 / 0.1143479m).Within(0.000001m));
            Assert.That(reversed.TickSpacing, Is.EqualTo(60));
            var reversedPrice = reversed.VirtualAmountBForPrice / reversed.VirtualAmountAForPrice;
            Assert.That(reversedPrice, Is.EqualTo(1 / 0.1143479m).Within(0.0001m));
        }

        [Test]
        public void ReservesAreTrackedIncrementallyFromEvents()
        {
            var pool = MakePool(0, 0, 0.1143479m);

            PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                AssetIdA = 0,
                AssetAmountA = 999995,
                AssetIdB = 10458941,
                AssetAmountB = 97004,
            });
            Assert.That(pool.A, Is.EqualTo(999995));
            Assert.That(pool.B, Is.EqualTo(97004));

            PoolReserveUpdater.ApplyTrade(pool, new Trade
            {
                AssetIdIn = 0,
                AssetAmountIn = 100000,
                AssetIdOut = 10458941,
                AssetAmountOut = 11526,
            });
            Assert.That(pool.A, Is.EqualTo(1099995));
            Assert.That(pool.B, Is.EqualTo(97004 - 11526));

            // reverse direction trade (asset B in)
            PoolReserveUpdater.ApplyTrade(pool, new Trade
            {
                AssetIdIn = 10458941,
                AssetAmountIn = 1000,
                AssetIdOut = 0,
                AssetAmountOut = 8000,
            });
            Assert.That(pool.A, Is.EqualTo(1091995));
            Assert.That(pool.B, Is.EqualTo(97004 - 11526 + 1000));

            PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity
            {
                Direction = LiquidityDirection.WithdrawLiquidity,
                AssetIdA = 0,
                AssetAmountA = 999987,
                AssetIdB = 10458941,
                AssetAmountB = 50000,
            });
            Assert.That(pool.A, Is.EqualTo(1091995 - 999987));
            Assert.That(pool.B, Is.EqualTo(97004 - 11526 + 1000 - 50000));

            // withdrawing more than tracked never underflows
            PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity
            {
                Direction = LiquidityDirection.WithdrawLiquidity,
                AssetIdA = 0,
                AssetAmountA = ulong.MaxValue / 2,
                AssetIdB = 10458941,
                AssetAmountB = ulong.MaxValue / 2,
            });
            Assert.That(pool.A, Is.EqualTo(0));
            Assert.That(pool.B, Is.EqualTo(0));
        }

        [Test]
        public void ClassicPoolsStillUseAbsoluteReserves()
        {
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.OldAMM,
                AssetIdA = 0,
                AssetIdB = 452399768,
                A = 10,
                B = 20,
                L = 5,
            };
            PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetAmountIn = 1, AssetIdOut = 452399768, AssetAmountOut = 1, A = 1397634483, B = 9117323945, L = 0 });
            Assert.That(pool.A, Is.EqualTo(1397634483));
            Assert.That(pool.B, Is.EqualTo(9117323945));
            Assert.That(pool.L, Is.EqualTo(5), "L must not be overwritten by 0");

            PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity { Direction = LiquidityDirection.DepositLiquidity, AssetIdA = 0, AssetAmountA = 1, AssetIdB = 452399768, AssetAmountB = 1, A = 100, B = 200, L = 300 });
            Assert.That(pool.A, Is.EqualTo(100));
            Assert.That(pool.B, Is.EqualTo(200));
            Assert.That(pool.L, Is.EqualTo(300));
        }
    }
}
