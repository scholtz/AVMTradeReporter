using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Processors.Pool
{

    public class PactPoolTests
    {
        [Test]
        public async Task LoadPactPoolAsync()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "M72TAR3CZLHFCC2JFIDCFDRICMDIB73FYMFETEKCEPGR5XO7ILFNUKEV34";
            ulong appId = 2_757_661_465;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(386195940));

            var json = Algorand.Utils.Encoder.EncodeToJson(pool);
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"M72TAR3CZLHFCC2JFIDCFDRICMDIB73FYMFETEKCEPGR5XO7ILFNUKEV34\",\r\n  \"PoolAppId\": 2757661465,\r\n  \"AssetIdA\": 0,\r\n  \"AssetADecimals\": 6,\r\n  \"AssetIdB\": 386195940,\r\n  \"AssetBDecimals\": 6,\r\n  \"AssetIdLP\": 2757661470,\r\n  \"A\": 88310359,\r\n  \"B\": 349128,\r\n  \"L\": 5434977,\r\n  \"Timestamp\": \"2026-01-17T08:27:09.2167776+01:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"85a9a6a24f463fcf7f07982f8f06761dc75dae0369b5002ecddd0c749500e7fa\",\r\n  \"LPFee\": 0.003,\r\n  \"ProtocolFeePortion\": 0.1666666666666666666666666667,\r\n  \"VirtualAmountA\": 88.310359,\r\n  \"RealAmountA\": 88.310359,\r\n  \"VirtualAmountB\": 0.349128,\r\n  \"RealAmountB\": 0.349128\r\n}"));


        }
        [Test]
        public async Task LoadPactPoolAsync645869114()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "IWT4WOUKYQBCAO76UKWZ5E4CPIJVLBE5R3NX5QH3BXMTG34WU7ZCLJ4RVY";
            ulong appId = 645869114;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(386195940));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.LPFee, Is.EqualTo(0.003m));
        }
        [Test]
        public async Task LoadPactPoolAsyncStableSwap()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "UA5STOUUVGT3ARYUCBQKXBE6L3XONZL3MOP7AC3VXP5AQA6NI5OUBRJ6UU";
            ulong appId = 1205810547;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.StableSwap));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(1185173782));
            Assert.That(pool.StableA, Is.GreaterThan(1));
            Assert.That(pool.StableB, Is.GreaterThan(1));
            Assert.That(pool.LPFee, Is.EqualTo(0.0015m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2));
        }
    }
}
