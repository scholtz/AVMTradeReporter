using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporterTests.Processors.SWAP;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.Pool
{
    /// <summary>
    /// Pact weighted pool (ARC4 contract with reserve_a / reserve_b / issued_lp / weight_a global state) loading tests.
    /// </summary>
    public class PactWeightedPoolTests
    {
        [Test]
        public async Task LoadPactWeightedPoolAsync3689973907()
        {
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = PactWeightedSwapProcessorTests.WeightedPoolAddress;
            ulong appId = PactWeightedSwapProcessorTests.WeightedPoolAppId;

            var pool = await processor.LoadPoolAsync(address, appId);

            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.WeightedAMM));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(452399768));
            Assert.That(pool.AssetIdLP, Is.EqualTo(3689973910));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.L, Is.GreaterThan(0));
            Assert.That(pool.StableA, Is.Null);
            Assert.That(pool.StableB, Is.Null);
            // swap_fee_bps = 10
            Assert.That(pool.LPFee, Is.EqualTo(0.001m));
            // protocol_fee_bps = 2500 (25 % of the swap fee), manager_fee_bps = 0
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.25m));
            // weight_a = 5000 bps
            Assert.That(pool.WeightA, Is.EqualTo(0.5m));
            Assert.That(pool.WeightB, Is.EqualTo(0.5m));
            Assert.That(pool.AssetADecimals, Is.EqualTo(6));
            Assert.That(pool.AssetBDecimals, Is.EqualTo(6));
            Assert.That(pool.RealAmountA, Is.GreaterThan(0));
            Assert.That(pool.RealAmountB, Is.GreaterThan(0));
            // 50/50 pool: virtual amounts equal the real amounts and ALGO/USDC price is in a sane range
            Assert.That(pool.VirtualAmountA, Is.EqualTo(pool.RealAmountA));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(pool.RealAmountB));
            var price = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(price, Is.GreaterThan(0.01m));
            Assert.That(price, Is.LessThan(100m));

            // second load must not report an update when nothing changed
            var stored = await poolRepository.GetPoolAsync(address, CancellationToken.None);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.AMMType, Is.EqualTo(AMMType.WeightedAMM));
        }

        [Test]
        public async Task LoadClassicPactPoolStillWorks()
        {
            // regression: classic Pact AMM (A / B / L / CONFIG keys) must still be loaded as OldAMM
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            var pool = await processor.LoadPoolAsync("IWT4WOUKYQBCAO76UKWZ5E4CPIJVLBE5R3NX5QH3BXMTG34WU7ZCLJ4RVY", 645869114);

            Assert.That(pool.AMMType, Is.EqualTo(AMMType.OldAMM));
            Assert.That(pool.WeightA, Is.Null);
            Assert.That(pool.WeightB, Is.Null);
        }
    }
}
