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
