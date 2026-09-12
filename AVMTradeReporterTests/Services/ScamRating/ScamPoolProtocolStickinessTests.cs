using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services.ScamRating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Services.ScamRating
{
    /// <summary>
    /// A pool rated as a known scam must be labelled <see cref="DEXProtocol.Scam"/> and stay that way:
    /// the next swap / liquidity event in that pool (which still carries the imitated protocol, e.g.
    /// Biatec) must not re-label it, and its balances must stay at 0.
    /// Uses the real <see cref="PoolRepository"/> (in-memory only, no Elasticsearch/Redis) wired to the
    /// real <see cref="ScamRatingService"/> with a mocked ARC-56 registry.
    /// </summary>
    public class ScamPoolProtocolStickinessTests
    {
        private const ulong KnownScamPoolAppId = 3680724745UL;
        private const string ScamPoolAddress = "SCAMPOOLADDRESS";

        private static PoolRepository CreateRepository()
        {
            var appConfig = new OptionsWrapper<AppConfiguration>(new AppConfiguration());
            var registry = new Mock<IArc56RegistryClient>();
            registry.Setup(r => r.IsApprovalProgramRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var service = new ScamRatingService(registry.Object, appConfig, new LoggerFactory().CreateLogger<ScamRatingService>());
            var aggregated = new AggregatedPoolRepository(null!, new LoggerFactory().CreateLogger<AggregatedPoolRepository>(), null!, appConfig, null!, null, null);
            return new PoolRepository(null!, new LoggerFactory().CreateLogger<PoolRepository>(), null!, aggregated, appConfig, null!, null, null, service);
        }

        private static PoolModel CreateScamPoolAsSeenByBiatecProcessor() => new()
        {
            PoolAddress = ScamPoolAddress,
            PoolAppId = KnownScamPoolAppId,
            Protocol = DEXProtocol.Biatec,
            AMMType = AMMType.ConcentratedLiquidityAMM,
            ApprovalProgramHash = "b30a4c3afb4c28418592ac45dd6cc2179cb3297f2aee18e9ea02d3ab1d836c73",
            AssetIdA = 1241945177,
            AssetIdB = 31566704,
            AssetADecimals = 6,
            AssetBDecimals = 6,
            A = 52_000_000_000,
            B = 63_000_000_000,
            L = 1,
            PMin = 0.9m,
            PMax = 0.9m,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
        };

        [Test]
        public async Task StoringKnownScamPool_LabelsItScamWithZeroBalances()
        {
            var repository = CreateRepository();
            var pool = CreateScamPoolAsSeenByBiatecProcessor();

            await repository.StorePoolAsync(pool, false, CancellationToken.None);
            var stored = await repository.GetPoolAsync(ScamPoolAddress, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.Not.Null);
                Assert.That(stored!.ScamRating, Is.EqualTo(100));
                Assert.That(stored.Protocol, Is.EqualTo(DEXProtocol.Scam));
                Assert.That(stored.A, Is.EqualTo(0UL));
                Assert.That(stored.B, Is.EqualTo(0UL));
            });
        }

        [Test]
        public async Task NextSwapInScamPool_DoesNotRewriteProtocolToBiatec_OrRestoreBalances()
        {
            var repository = CreateRepository();
            await repository.StorePoolAsync(CreateScamPoolAsSeenByBiatecProcessor(), false, CancellationToken.None);

            var trade = new Trade
            {
                TxId = "SWAP1",
                PoolAddress = ScamPoolAddress,
                PoolAppId = KnownScamPoolAppId,
                Protocol = DEXProtocol.Biatec, // the swap processor still recognises the fake layout as Biatec
                TradeState = TxState.Confirmed,
                Timestamp = DateTimeOffset.UtcNow,
                AssetIdIn = 1241945177,
                AssetIdOut = 31566704,
                AssetAmountIn = 1_000_000,
                AssetAmountOut = 900_000,
                A = 53_000_000_000,
                B = 62_100_000_000,
                L = 1,
                ValueUSD = 1m,
            };

            await repository.UpdatePoolFromTrade(trade, CancellationToken.None);
            var stored = await repository.GetPoolAsync(ScamPoolAddress, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.Not.Null);
                Assert.That(stored!.Protocol, Is.EqualTo(DEXProtocol.Scam));
                Assert.That(stored.ScamRating, Is.EqualTo(100));
                Assert.That(stored.A, Is.EqualTo(0UL));
                Assert.That(stored.B, Is.EqualTo(0UL));
                Assert.That(stored.Timestamp, Is.EqualTo(trade.Timestamp), "the update itself is still recorded");
            });
        }

        [Test]
        public async Task NextLiquidityEventInScamPool_DoesNotRewriteProtocol_OrRestoreBalances()
        {
            var repository = CreateRepository();
            await repository.StorePoolAsync(CreateScamPoolAsSeenByBiatecProcessor(), false, CancellationToken.None);

            var liquidity = new Liquidity
            {
                TxId = "LIQ1",
                PoolAddress = ScamPoolAddress,
                PoolAppId = KnownScamPoolAppId,
                Protocol = DEXProtocol.Biatec,
                Direction = LiquidityDirection.DepositLiquidity,
                TxState = TxState.Confirmed,
                Timestamp = DateTimeOffset.UtcNow,
                AssetIdA = 1241945177,
                AssetIdB = 31566704,
                A = 60_000_000_000,
                B = 70_000_000_000,
                L = 2,
            };

            await repository.UpdatePoolFromLiquidity(liquidity, CancellationToken.None);
            var stored = await repository.GetPoolAsync(ScamPoolAddress, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.Not.Null);
                Assert.That(stored!.Protocol, Is.EqualTo(DEXProtocol.Scam));
                Assert.That(stored.A, Is.EqualTo(0UL));
                Assert.That(stored.B, Is.EqualTo(0UL));
            });
        }

        [Test]
        public async Task PoolProcessorRefreshOfScamPool_IsRelabelledScamAgainOnStore()
        {
            // BiatecPoolProcessor.LoadPoolAsync mutates the cached instance back to Protocol=Biatec with
            // fresh on-chain balances and then calls StorePoolAsync - the store must undo both.
            var repository = CreateRepository();
            await repository.StorePoolAsync(CreateScamPoolAsSeenByBiatecProcessor(), false, CancellationToken.None);
            var cached = (await repository.GetPoolAsync(ScamPoolAddress, CancellationToken.None))!;

            cached.Protocol = DEXProtocol.Biatec;
            cached.A = 52_000_000_000;
            cached.B = 63_000_000_000;
            await repository.StorePoolAsync(cached, false, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(cached.Protocol, Is.EqualTo(DEXProtocol.Scam));
                Assert.That(cached.A, Is.EqualTo(0UL));
                Assert.That(cached.B, Is.EqualTo(0UL));
            });
        }

        [Test]
        public async Task HonestPool_KeepsItsProtocolAndBalancesAcrossTrades()
        {
            var repository = CreateRepository();
            var honest = CreateScamPoolAsSeenByBiatecProcessor();
            honest.PoolAddress = "HONESTPOOL";
            honest.PoolAppId = 1;
            honest.ApprovalProgramHash = "d5af0104faa636835883acc9096d9908b4a1e2caa6fe8d662e62b141ec8b41b1";
            await repository.StorePoolAsync(honest, false, CancellationToken.None);

            await repository.UpdatePoolFromTrade(new Trade
            {
                TxId = "SWAP2",
                PoolAddress = "HONESTPOOL",
                PoolAppId = 1,
                Protocol = DEXProtocol.Biatec,
                TradeState = TxState.Confirmed,
                Timestamp = DateTimeOffset.UtcNow,
                A = 53_000_000_000,
                B = 62_100_000_000,
                L = 1,
            }, CancellationToken.None);
            var stored = await repository.GetPoolAsync("HONESTPOOL", CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(stored!.Protocol, Is.EqualTo(DEXProtocol.Biatec));
                Assert.That(stored.ScamRating, Is.EqualTo(0));
                Assert.That(stored.A, Is.EqualTo(53_000_000_000UL));
                Assert.That(stored.B, Is.EqualTo(62_100_000_000UL));
            });
        }
    }
}
