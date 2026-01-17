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
        public async Task LoadBiatecPoolAsync3131562380Update()
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
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(1241945177));
            Assert.That(pool.PMin, Is.Not.Null, "PMin should not be null");
            Assert.That(pool.PMax, Is.Not.Null, "PMax should not be null");
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin!.Value));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax!.Value));

            pool.A = 100;
            pool.B = 200;
            pool.LPFee = 0;
            pool.ProtocolFeePortion = 0;

            var pool2 = await processor.LoadPoolAsync(address, appId);

            Assert.That(pool2.PMin, Is.Not.Null, "PMin should not be null");
            Assert.That(pool2.PMax, Is.Not.Null, "PMax should not be null");
            Assert.That(pool2.VirtualAmountB / pool2.VirtualAmountA, Is.GreaterThanOrEqualTo(pool2.PMin!.Value));
            Assert.That(pool2.VirtualAmountB / pool2.VirtualAmountA, Is.LessThanOrEqualTo(pool2.PMax!.Value));
            Assert.That(pool.LPFee, Is.EqualTo(0.0001m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2m));

        }
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
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(1241945177));

            Assert.That(pool.PMin, Is.Not.Null, "PMin should not be null");
            Assert.That(pool.PMax, Is.Not.Null, "PMax should not be null");
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin!.Value));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax!.Value));
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
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(452399768));
            Assert.That(pool.AssetIdB, Is.EqualTo(0));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.L, Is.GreaterThan(0));
            Assert.That(pool.PMin, Is.GreaterThan(0));
            Assert.That(pool.PMax, Is.GreaterThan(0));
            Assert.That(pool.LPFee, Is.EqualTo(0.0001m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2m));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin!.Value));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax!.Value));
        }
        [Test]
        public async Task LoadBiatecPoolAsync3098469455()
        {
            // Arrange

            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(AlgodConfiguration.MainNet);
            DefaultApi algod = new DefaultApi(httpClient);
            var logger = new LoggerFactory().CreateLogger<BiatecPoolProcessor>();

            var poolRepository = new MockPoolRepository();
            var processor = new AVMTradeReporter.Processors.Pool.BiatecPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = "LBLOB5IO3UZX3JOBY2MHDLPEWWIKMPYIISAKLABX4B7S4SPG2W65OQXOKQ";
            ulong appId = 3098469455;
            // Act
            var pool = await processor.LoadPoolAsync(address, appId);
            // Assert
            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(1241945177));
            Assert.That(pool.AssetIdB, Is.EqualTo(31566704));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            Assert.That(pool.L, Is.GreaterThan(0));
            Assert.That(pool.PMin, Is.GreaterThan(0));
            Assert.That(pool.PMax, Is.GreaterThan(0));
            Assert.That(pool.LPFee, Is.EqualTo(0.001m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2m));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.GreaterThanOrEqualTo(pool.PMin!.Value));
            Assert.That(pool.VirtualAmountB / pool.VirtualAmountA, Is.LessThanOrEqualTo(pool.PMax!.Value));
        }
    }
}
