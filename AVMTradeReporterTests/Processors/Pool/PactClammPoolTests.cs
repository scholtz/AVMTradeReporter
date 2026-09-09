using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporterTests.Processors;
using AVMTradeReporterTests.Processors.SWAP;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.Pool
{
    /// <summary>
    /// Pact CLAMM (tick based concentrated liquidity) pool loading tests against the testnet pool 763274703.
    /// </summary>
    public class PactClammPoolTests
    {
        [Test]
        public async Task LoadPactClammPoolAsync763274703()
        {
            var algod = TestNetBlocks.CreateAlgod();
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();
            var poolRepository = new MockPoolRepository();
            var processor = new PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = PactClammSwapProcessorTests.ClammPoolAddress;
            ulong appId = PactClammSwapProcessorTests.ClammPoolAppId;

            var pool = await processor.LoadPoolAsync(address, appId);

            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(PactClammSwapProcessorTests.AssetB));
            // no LP token, no reserves in the contract state
            Assert.That(pool.AssetIdLP, Is.EqualTo(0));
            Assert.That(pool.A, Is.EqualTo(0));
            Assert.That(pool.B, Is.EqualTo(0));
            Assert.That(pool.StableA, Is.Null);
            // fee_bps = 30, protocol_fee_bps = 2000 (20 % of the fee)
            Assert.That(pool.LPFee, Is.EqualTo(0.003m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2m));
            Assert.That(pool.TickSpacing, Is.EqualTo(60));
            Assert.That(pool.CurrentTick, Is.GreaterThan(0));
            // sqrt price Q64.64 decoded to a decimal price of 1 ALGO in USDC (both 6 decimals)
            Assert.That(pool.CurrentPrice, Is.GreaterThan(0.001m));
            Assert.That(pool.CurrentPrice, Is.LessThan(100m));
            Assert.That(pool.AssetADecimals, Is.EqualTo(6));
            Assert.That(pool.AssetBDecimals, Is.EqualTo(6));

            var stored = await poolRepository.GetPoolAsync(address, CancellationToken.None);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));

            // second load keeps the reserves that were tracked from events
            stored.A = 5_000_000;
            stored.B = 571_000;
            var reloaded = await processor.LoadPoolAsync(address, appId);
            Assert.That(reloaded.A, Is.EqualTo(5_000_000));
            Assert.That(reloaded.B, Is.EqualTo(571_000));
            Assert.That(reloaded.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));
        }
    }
}
