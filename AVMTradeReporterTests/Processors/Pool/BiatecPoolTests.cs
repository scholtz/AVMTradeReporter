using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Processors.Pool
{

    public class BiatecPoolTests
    {
        [Test]
        public async Task LoadBiatecPoolAsync()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<BiatecPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.BiatecPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "43KNWWESA43DEGHSRJVRU55V5IBOCJSPXH3OSBV4IDOGS6VYIDYAXYHDGU";
            ulong appId = 3131562380;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(1241945177));

            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax));
        }

        [Test]
        public async Task LoadBiatecPoolAsync3136517663()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<BiatecPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.BiatecPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "RT5KAKAZZS7IPGTDXKP27LS7I2M5VBX336YA3VP4UKEDS2UVOWHPTKR5QE";
            ulong appId = 3136517663;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.IsNotNull(pool);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(452399768));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.L, Is.GreaterThan(0));
            Assert.That(pool.PMin, Is.GreaterThan(0));
            Assert.That(pool.PMax, Is.GreaterThan(0));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax));
        }
    }
}
