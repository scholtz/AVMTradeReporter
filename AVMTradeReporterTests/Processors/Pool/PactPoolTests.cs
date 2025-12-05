using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;

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
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(386195940));

            var json = Algorand.Utils.Encoder.EncodeToJson(pool);
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"M72TAR3CZLHFCC2JFIDCFDRICMDIB73FYMFETEKCEPGR5XO7ILFNUKEV34\",\r\n  \"PoolAppId\": 2757661465,\r\n  \"AssetIdA\": 0,\r\n  \"AssetADecimals\": 6,\r\n  \"AssetIdB\": 386195940,\r\n  \"AssetBDecimals\": 6,\r\n  \"AssetIdLP\": 2757661470,\r\n  \"A\": 80522993,\r\n  \"B\": 381929,\r\n  \"L\": 5434977,\r\n  \"Timestamp\": \"2025-11-29T15:15:46.8006251+01:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"85a9a6a24f463fcf7f07982f8f06761dc75dae0369b5002ecddd0c749500e7fa\",\r\n  \"LPFee\": 0.003,\r\n  \"ProtocolFeePortion\": 0.1666666666666666666666666667,\r\n  \"VirtualAmountA\": 80.522993,\r\n  \"RealAmountA\": 80.522993,\r\n  \"VirtualAmountB\": 0.381929,\r\n  \"RealAmountB\": 0.381929\r\n}"));


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
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(386195940));
            Assert.That(pool.A, Is.GreaterThan(119972741391));
            Assert.That(pool.B, Is.GreaterThan(642807254));
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
            Assert.That(pool, Is.Not.Null);
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
        [Test]
        public async Task LoadPactPoolAsyncStableSwap2()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "YHHVVYHF7NHZZOJOTT2ENRAOCSYIYF6RQLXKAY5N2QEFJESWXMRAXCNIZ4";
            ulong appId = 2746842986;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.StableSwap));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(1185173782));
            Assert.That(pool.A, Is.EqualTo(0));
            Assert.That(pool.B, Is.EqualTo(0));
            Assert.That(pool.LPFee, Is.EqualTo(0.0015m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2));
            Assert.That(pool.RealAmountA, Is.GreaterThan(0));
            Assert.That(pool.RealAmountB, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountA, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountB, Is.GreaterThan(0));
        }
        [Test]
        public async Task LoadPactPoolAsyncStableSwapAramidAlgo()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "6JY46UJCKEFGQQZLDEMQH64PHLA2KQT65OB3TZXGVOIR6M7PB2D5N6Z35A";
            ulong appId = 2746883572;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.StableSwap));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(2320804780));
            Assert.That(pool.A, Is.EqualTo(0));
            Assert.That(pool.B, Is.EqualTo(0));
            Assert.That(pool.LPFee, Is.EqualTo(0.0015m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2));
            Assert.That(pool.RealAmountA, Is.GreaterThan(0));
            Assert.That(pool.RealAmountB, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountA, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountB, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThan(0.9));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThan(1.1));
        }
    }
}
