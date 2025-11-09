using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
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
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()), null, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong TEST_ASSET = 1234UL;
            const ulong USDC = 31566704UL;

            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool-asset-usdc",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 1_000_000, // 1
                B = 2_000_000, // 2
                AF = 0,
                BF = 0,
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
            
            // Real TVL = only trusted token (USDC) side: 2 * 1 = 2
            Assert.That(asset.TVL_USD, Is.EqualTo(2m));
            
            // Total TVL = both sides: (1*2) + (2*1) = 4
            Assert.That(asset.TotalTVLAssetInUSD, Is.EqualTo(4m));
        }

        [Test]
        public async Task AssetPriceDerivedViaAlgo_WhenNoDirectUsdcPool()
        {
            // Arrange
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var mockAssetRepo = new MockAssetRepository();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()), null, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong ALGO = 0UL;
            const ulong USDC = 31566704UL;
            const ulong TEST_ASSET = 5678UL;

            // ALGO-USDC pool (ALGO price reference). 10 ALGO vs 2.5 USDC -> ALGO price 0.25
            var algoUsdcPool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool-algo-usdc",
                AssetIdA = ALGO,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 10_000_000, // 10
                B = 2_500_000,  // 2.5
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow
            };

            // TEST_ASSET - ALGO pool: 4 asset vs 2 ALGO -> 0.5 ALGO per asset -> price 0.5 * 0.25 = 0.125 USD
            var assetAlgoPool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool-asset-algo",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = ALGO,
                AssetBDecimals = 6,
                A = 4_000_000, // 4
                B = 2_000_000, // 2
                AF = 0,
                BF = 0,
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
            
            // Real TVL for asset = only the ALGO side: 2 ALGO * 0.25 = 0.5
            Assert.That(asset.TVL_USD, Is.EqualTo(0.5m));
            
            // Total TVL for asset = both sides: (4 * 0.125) + (2 * 0.25) = 0.5 + 0.5 = 1.0
            Assert.That(asset.TotalTVLAssetInUSD, Is.EqualTo(1.0m));
        }

        [Test]
        public async Task RealTVL_OnlyCountsTrustedTokenSide()
        {
            // Arrange
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var mockAssetRepo = new MockAssetRepository();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()), null, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong TEST_ASSET = 9999UL;
            const ulong USDC = 31566704UL;

            // Direct TEST_ASSET-USDC pool: 100 TEST_ASSET vs 50 USDC
            // Asset price = 50/100 = 0.5 USD per TEST_ASSET
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool-test-usdc",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 100_000_000, // 100
                B = 50_000_000,  // 50
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            await poolRepository.StorePoolAsync(pool, true, ct.Token);
            await poolRepository.UpdateAggregatedPool(TEST_ASSET, USDC, ct.Token);

            var asset = await mockAssetRepo.GetAssetAsync(TEST_ASSET, ct.Token) ?? throw new Exception("Asset not found");

            // Assert
            Assert.That(asset.PriceUSD, Is.EqualTo(0.5m), "Asset price should be 0.5 USD");
            
            // Real TVL = only USDC side: 50 USDC × 1 = 50 USD
            Assert.That(asset.TVL_USD, Is.EqualTo(50m), "Real TVL should only count the trusted token (USDC) side");
            
            // Total TVL = both sides: (100 × 0.5) + (50 × 1) = 50 + 50 = 100 USD
            Assert.That(asset.TotalTVLAssetInUSD, Is.EqualTo(100m), "Total TVL should count both sides of the pool");
        }

        [Test]
        public async Task MultiplePools_RealAndTotalTVL_AggregateCorrectly()
        {
            // Arrange
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var mockAssetRepo = new MockAssetRepository();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()), null, mockAssetRepo);
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var poolRepository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var ct = new CancellationTokenSource();

            const ulong ALGO = 0UL;
            const ulong USDC = 31566704UL;
            const ulong TEST_ASSET = 7777UL;

            // Setup ALGO-USDC pool for ALGO price: 4 ALGO vs 1 USDC -> ALGO = 0.25 USD
            var algoUsdcPool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool-algo-usdc",
                AssetIdA = ALGO,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 4_000_000, // 4
                B = 1_000_000, // 1
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Pool 1: TEST_ASSET-USDC: 10 TEST_ASSET vs 20 USDC -> price = 2 USD
            var pool1 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool1-asset-usdc",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = USDC,
                AssetBDecimals = 6,
                A = 10_000_000, // 10
                B = 20_000_000, // 20
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(1)
            };

            // Pool 2: TEST_ASSET-ALGO: 8 TEST_ASSET vs 64 ALGO -> price via ALGO = 64/8 * 0.25 = 2 USD (consistent)
            var pool2 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "pool2-asset-algo",
                AssetIdA = TEST_ASSET,
                AssetADecimals = 6,
                AssetIdB = ALGO,
                AssetBDecimals = 6,
                A = 8_000_000,  // 8
                B = 64_000_000, // 64
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(2)
            };

            // Act
            await poolRepository.StorePoolAsync(algoUsdcPool, true, ct.Token);
            await poolRepository.StorePoolAsync(pool1, true, ct.Token);
            await poolRepository.StorePoolAsync(pool2, true, ct.Token);
            await poolRepository.UpdateAggregatedPool(ALGO, USDC, ct.Token);
            await poolRepository.UpdateAggregatedPool(TEST_ASSET, USDC, ct.Token);
            await poolRepository.UpdateAggregatedPool(TEST_ASSET, ALGO, ct.Token);

            var asset = await mockAssetRepo.GetAssetAsync(TEST_ASSET, ct.Token) ?? throw new Exception("Asset not found");

            // Assert
            Assert.That(asset.PriceUSD, Is.EqualTo(2m), "Asset price should be 2 USD");
            
            // Real TVL = trusted tokens only:
            // Pool 1: 20 USDC × 1 = 20 USD
            // Pool 2: 64 ALGO × 0.25 = 16 USD
            // Total Real TVL = 20 + 16 = 36 USD
            Assert.That(asset.TVL_USD, Is.EqualTo(36m), "Real TVL should sum only trusted token sides from both pools");
            
            // Total TVL = both sides:
            // Pool 1: (10 × 2) + (20 × 1) = 20 + 20 = 40 USD
            // Pool 2: (8 × 2) + (64 × 0.25) = 16 + 16 = 32 USD
            // Total TVL = 40 + 32 = 72 USD
            Assert.That(asset.TotalTVLAssetInUSD, Is.EqualTo(72m), "Total TVL should sum both sides from both pools");
        }
    }
}
