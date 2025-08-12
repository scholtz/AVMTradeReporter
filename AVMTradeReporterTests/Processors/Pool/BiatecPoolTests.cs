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

            var json = Algorand.Utils.Encoder.EncodeToJson(pool);
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"43KNWWESA43DEGHSRJVRU55V5IBOCJSPXH3OSBV4IDOGS6VYIDYAXYHDGU\",\r\n  \"PoolAppId\": 3131562380,\r\n  \"AssetIdA\": 0,\r\n  \"AssetADecimals\": 6,\r\n  \"AssetIdB\": 1241945177,\r\n  \"AssetBDecimals\": 6,\r\n  \"AssetIdLP\": 3131562383,\r\n  \"A\": 0,\r\n  \"B\": 0,\r\n  \"L\": 0,\r\n  \"PMin\": 0.3,\r\n  \"PMax\": 0.33,\r\n  \"Protocol\": 2,\r\n  \"Timestamp\": \"2025-08-12T19:38:48.6953626+02:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"af89b67168c410fb94b2dae735dd2483071d7ef0114de56e53aa2f15f681637a\",\r\n  \"LPFee\": 0.0,\r\n  \"ProtocolFeePortion\": 0.0\r\n}"));

        }
    }
}
