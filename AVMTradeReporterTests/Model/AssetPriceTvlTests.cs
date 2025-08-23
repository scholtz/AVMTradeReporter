using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Model
{
    public class AssetPriceTvlTests
    {
        [Test]
        public async Task DirectAssetUsdcPool_ComputesPriceAndTVL()
        {
            // Arrange
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var mockAssetRepo = new MockAssetRepository();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong TEST_ASSET = 1234UL;
            const ulong USDC = 31566704UL;

            var pool = new AVMTradeReporter.Model.Data.Pool
            {
                PoolAddress = "pool-asset-usdc",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 1_000_000, // 1
                B = 2_000_000, // 2
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            await poolRepository.StorePoolAsync(pool, true, ct.Token);
            // ensure aggregated pool updated
            await poolRepository.UpdateAggregatedPool(TEST_ASSET, USDC, ct.Token);

            var aggregated = aggregatedPoolsRepository.GetAggregatedPool(TEST_ASSET, USDC)!;
            var asset = await mockAssetRepo.GetAssetAsync(TEST_ASSET, ct.Token) ?? throw new Exception("Asset not found");
            var usdc = await mockAssetRepo.GetAssetAsync(USDC, ct.Token) ?? throw new Exception("USDC not found");

            // Assert price = 2 / 1 = 2
            Assert.That(aggregated.VirtualSumALevel1, Is.EqualTo(1m));
            Assert.That(aggregated.VirtualSumBLevel1, Is.EqualTo(2m));
            Assert.That(asset.PriceUSD, Is.EqualTo(2m));
            Assert.That(usdc.PriceUSD, Is.EqualTo(1m));
            // TVL_USD = (1*2) + (2*1) = 4
            Assert.That(asset.TVL_USD, Is.EqualTo(4m));
        }

        [Test]
        public async Task AssetPriceDerivedViaAlgo_WhenNoDirectUsdcPool()
        {
            // Arrange
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var mockAssetRepo = new MockAssetRepository();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong ALGO = 0UL;
            const ulong USDC = 31566704UL;
            const ulong TEST_ASSET = 5678UL;

            // ALGO-USDC pool (ALGO price reference). 10 ALGO vs 2.5 USDC -> ALGO price 0.25
            var algoUsdcPool = new AVMTradeReporter.Model.Data.Pool
            {
                PoolAddress = "pool-algo-usdc",
                AssetIdA = ALGO,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 10_000_000, // 10
                B = 2_500_000,  // 2.5
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow
            };

            // TEST_ASSET - ALGO pool: 4 asset vs 2 ALGO -> 0.5 ALGO per asset -> price 0.5 * 0.25 = 0.125 USD
            var assetAlgoPool = new AVMTradeReporter.Model.Data.Pool
            {
                PoolAddress = "pool-asset-algo",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = ALGO,
                AssetBDecimals = 6,
                A = 4_000_000, // 4
                B = 2_000_000, // 2
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(1)
            };

            // Act
            await poolRepository.StorePoolAsync(algoUsdcPool, true, ct.Token);
            await poolRepository.StorePoolAsync(assetAlgoPool, true, ct.Token);
            await poolRepository.UpdateAggregatedPool(ALGO, USDC, ct.Token);
            await poolRepository.UpdateAggregatedPool(TEST_ASSET, ALGO, ct.Token);

            var algo = await mockAssetRepo.GetAssetAsync(ALGO, ct.Token) ?? throw new Exception("ALGO not found");
            var asset = await mockAssetRepo.GetAssetAsync(TEST_ASSET, ct.Token) ?? throw new Exception("Asset not found");

            // Assert ALGO price
            Assert.That(algo.PriceUSD, Is.EqualTo(0.25m));
            // Assert derived asset price
            Assert.That(asset.PriceUSD, Is.EqualTo(0.125m));
            // TVL_USD for asset = (4 * 0.125) + (2 * 0.25) = 0.5 + 0.5 = 1.0
            Assert.That(asset.TVL_USD, Is.EqualTo(1.0m));
        }
    }
}
