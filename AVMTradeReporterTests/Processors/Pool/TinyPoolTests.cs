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

    public class TinyPoolTests
    {
        [Test]
        public async Task LoadTinyPoolAsync()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<TinyPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.TinyPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "2JPTIQWQEWFA6LIHCIXQ24LBDY7ZJ2DHKC3X5FPEITLMDB4C2BDIOXN6QE";
            ulong appId = 1002541853;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(3032713424));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
            var json = Algorand.Utils.Encoder.EncodeToJson(pool);
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"2JPTIQWQEWFA6LIHCIXQ24LBDY7ZJ2DHKC3X5FPEITLMDB4C2BDIOXN6QE\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"AssetIdA\": 3032713424,\r\n  \"AssetIdB\": 0,\r\n  \"AssetIdLP\": 3110834238,\r\n  \"A\": 368125256139613079,\r\n  \"B\": 28305414264,\r\n  \"AF\": 789717611529666,\r\n  \"BF\": 46772283,\r\n  \"Protocol\": 1,\r\n  \"Timestamp\": \"2025-11-29T15:18:30.3164133+01:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"dd63834ddcd51013ec0a22142497ad4c6d74e421e6c79149422c243346691f56\",\r\n  \"LPFee\": 0.003,\r\n  \"ProtocolFeePortion\": 0.2,\r\n  \"VirtualAmountA\": 368914973751142745.0,\r\n  \"RealAmountA\": 368914973751142745.0,\r\n  \"VirtualAmountB\": 28352186547.0,\r\n  \"RealAmountB\": 28352186547.0\r\n}"));

        }
        [Test]
        public async Task LoadTinyPoolTest1002541853TryFix()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<TinyPoolProcessor>();

            using var cancellationTokenSource = new CancellationTokenSource();

            var poolRepository = new MockPoolRepository();

            await poolRepository.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool()
            {
                PoolAddress = "2PIFZW53RHCSFSYMCFUBW4XOCXOMB7XOYQSQ6KGT3KVGJTL4HM6COZRNMM",
                PoolAppId = 1002541853,
                AssetIdA = 31566704,
                AssetIdB = 0,
                Protocol = DEXProtocol.Pact,
                ApprovalProgramHash = "hash",
                Timestamp = DateTimeOffset.UtcNow
            }, false, cancellationTokenSource.Token);

            var processor = new AVMTradeReporter.Processors.Pool.TinyPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "2PIFZW53RHCSFSYMCFUBW4XOCXOMB7XOYQSQ6KGT3KVGJTL4HM6COZRNMM";
            ulong appId = 1002541853;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Tiny));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(31566704));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
        }
        [Test]
        public async Task LoadTinyPoolTest1002541853()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<TinyPoolProcessor>();

            using var cancellationTokenSource = new CancellationTokenSource();

            var poolRepository = new MockPoolRepository();

            await poolRepository.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool()
            {
                PoolAddress = "E3CM5G2PMOS2IDKWLQDUSXUKUPJNY4HM4XOS4GJD2STQB7EJJC4HJLIXFE",
                PoolAppId = 1002541853,
                AssetIdA = 0,
                AssetIdB = 0,
                Protocol = DEXProtocol.Tiny,
                ApprovalProgramHash = "hash",
                Timestamp = DateTimeOffset.UtcNow
            }, false, cancellationTokenSource.Token);

            var processor = new AVMTradeReporter.Processors.Pool.TinyPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "E3CM5G2PMOS2IDKWLQDUSXUKUPJNY4HM4XOS4GJD2STQB7EJJC4HJLIXFE";
            ulong appId = 1002541853;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Tiny));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(2175930910));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.AF, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.BF, Is.GreaterThan(0));

        }
    }
}
