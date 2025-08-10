using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Model.Data;
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
            var processor = new AVMTradeReporter.Processors.Pool.TinyPoolProcessor(algod, poolRepository, logger);
            string address = "2JPTIQWQEWFA6LIHCIXQ24LBDY7ZJ2DHKC3X5FPEITLMDB4C2BDIOXN6QE";
            ulong appId = 1002541853;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(3032713424));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
            var json = Algorand.Utils.Encoder.EncodeToJson(pool);
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"2JPTIQWQEWFA6LIHCIXQ24LBDY7ZJ2DHKC3X5FPEITLMDB4C2BDIOXN6QE\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"AssetIdA\": 3032713424,\r\n  \"AssetIdB\": 0,\r\n  \"AssetIdLP\": 3110834238,\r\n  \"A\": 440530209446717804,\r\n  \"B\": 23327203827,\r\n  \"AF\": 287918143022020,\r\n  \"BF\": 11262516,\r\n  \"Protocol\": 1,\r\n  \"Timestamp\": \"2025-08-10T22:52:15.1392537+02:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"dd63834ddcd51013ec0a22142497ad4c6d74e421e6c79149422c243346691f56\",\r\n  \"LPFee\": 0.003,\r\n  \"ProtocolFeePortion\": 0.2\r\n}"));

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

            await poolRepository.StorePoolAsync(new AVMTradeReporter.Model.Data.Pool()
            {
                PoolAddress = "2PIFZW53RHCSFSYMCFUBW4XOCXOMB7XOYQSQ6KGT3KVGJTL4HM6COZRNMM",
                PoolAppId = 1002541853,
                AssetIdA = 31566704,
                AssetIdB = 0,
                Protocol = DEXProtocol.Pact,
                ApprovalProgramHash = "hash",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationTokenSource.Token);

            var processor = new AVMTradeReporter.Processors.Pool.TinyPoolProcessor(algod, poolRepository, logger);
            string address = "2PIFZW53RHCSFSYMCFUBW4XOCXOMB7XOYQSQ6KGT3KVGJTL4HM6COZRNMM";
            ulong appId = 1002541853;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Tiny));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(31566704));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
        }
    }
}
