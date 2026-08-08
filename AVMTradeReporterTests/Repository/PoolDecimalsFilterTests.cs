using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// GetPoolsAsync used to silently drop every pool whose asset decimals could not
    /// be enriched (asset repository unavailable, algod hiccup, cold cache after a
    /// restart). For Biatec CLAMM pools the served amounts (A/B are stored in the
    /// 1e9 base scale) do not depend on asset decimals at all, so dropping them
    /// intermittently emptied the DEX's pool liquidity depth chart while the
    /// on-chain fallback views kept showing the pools.
    /// </summary>
    public class PoolDecimalsFilterTests
    {
        private static PoolRepository CreatePoolRepository()
        {
            var logger = new LoggerFactory().CreateLogger<PoolRepository>();
            var aggregatedLogger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregated = new AggregatedPoolRepository(null!, aggregatedLogger, null!, Options.Create(new AppConfiguration()), null!, null, null);
            // No asset repository -> decimals enrichment cannot run, like a cold start
            // with algod unavailable.
            return new PoolRepository(null!, logger, null!, aggregated, new OptionsWrapper<AppConfiguration>(new AppConfiguration()), null!, null, null);
        }

        [Test]
        public async Task GetPoolsAsync_ReturnsBiatecPoolWithoutDecimals()
        {
            var repository = CreatePoolRepository();
            var ct = CancellationToken.None;
            const ulong ASSET_A = 987653001UL;
            const ulong ASSET_B = 987653002UL;
            var pool = new PoolModel
            {
                PoolAddress = "decimals-biatec-pool",
                PoolAppId = 4242,
                AssetIdA = ASSET_A,
                AssetIdB = ASSET_B,
                // Deliberately no AssetADecimals / AssetBDecimals.
                A = 23_908_445_000,
                B = 493_150_000,
                L = 274_392_761_265,
                PMin = 0.13m,
                PMax = 0.14m,
                Protocol = DEXProtocol.Biatec,
                AMMType = AMMType.ConcentratedLiquidityAMM,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.StorePoolAsync(pool, false, ct);

            var result = await repository.GetPoolsAsync(ASSET_A, ASSET_B, null, null, 100, cancellationToken: ct);

            Assert.That(result.Select(p => p.PoolAddress), Does.Contain("decimals-biatec-pool"),
                "A Biatec pool must be served even when decimals enrichment failed — its base-scale amounts do not need decimals.");
        }

        [Test]
        public async Task GetPoolsAsync_StillDropsNonBiatecPoolWithoutDecimals()
        {
            var repository = CreatePoolRepository();
            var ct = CancellationToken.None;
            const ulong ASSET_A = 987654001UL;
            const ulong ASSET_B = 987654002UL;
            var pool = new PoolModel
            {
                PoolAddress = "decimals-tiny-pool",
                PoolAppId = 4243,
                AssetIdA = ASSET_A,
                AssetIdB = ASSET_B,
                // No decimals: a Tiny/Pact pool's amounts are stored in native units,
                // so without decimals the served values would be wrong by 10^decimals.
                A = 43_043_465_111,
                B = 5_354_594_830,
                Protocol = DEXProtocol.Tiny,
                AMMType = AMMType.OldAMM,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.StorePoolAsync(pool, false, ct);

            var result = await repository.GetPoolsAsync(ASSET_A, ASSET_B, null, null, 100, cancellationToken: ct);

            Assert.That(result, Is.Empty,
                "Non-Biatec pools without decimals would serve wrongly scaled amounts and must stay excluded.");
        }
    }
}
