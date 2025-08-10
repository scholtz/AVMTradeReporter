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
            var processor = new AVMTradeReporter.Processors.Pool.PactPoolProcessor(algod, poolRepository, logger);
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
            Assert.That(json, Is.EqualTo("{\r\n  \"PoolAddress\": \"M72TAR3CZLHFCC2JFIDCFDRICMDIB73FYMFETEKCEPGR5XO7ILFNUKEV34\",\r\n  \"PoolAppId\": 2757661465,\r\n  \"AssetIdA\": 0,\r\n  \"AssetIdB\": 386195940,\r\n  \"AssetIdLP\": 2757661470,\r\n  \"A\": 2016936829,\r\n  \"B\": 12700456,\r\n  \"L\": 158246466,\r\n  \"Timestamp\": \"2025-08-10T22:53:57.8551458+02:00\",\r\n  \"AMMType\": 0,\r\n  \"ApprovalProgramHash\": \"85a9a6a24f463fcf7f07982f8f06761dc75dae0369b5002ecddd0c749500e7fa\",\r\n  \"LPFee\": 0.003,\r\n  \"ProtocolFeePortion\": 0.1666666666666666666666666667\r\n}"));


        }
    }
}
