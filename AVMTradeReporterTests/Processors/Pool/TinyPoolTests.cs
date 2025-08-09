using Algorand;
using Algorand.Algod;
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
    }
}
