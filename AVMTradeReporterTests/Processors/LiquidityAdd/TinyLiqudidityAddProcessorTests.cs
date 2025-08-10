using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class TinyLiqudidityAddProcessorTests
    {

        [Test]
        public async Task TinyLiqudidityAddProcessorTestAsaAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            //var block = await client.FetchBlockAsync(52492471);
            var block = await client.FetchBlockAsync(52503607);
            
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdA\": 2726252423,\r\n  \"AssetIdLP\": 2741164994,\r\n  \"AssetAmountA\": 65000000000,\r\n  \"AssetAmountB\": 3520284290,\r\n  \"AssetAmountLP\": 11922817180,\r\n  \"A\": 4308628690303,\r\n  \"B\": 233347659875,\r\n  \"TxId\": \"Y47M2UIOJETTPFV7OPKULDUVBYDIJFORFVIM6UN4MRGF47TJBHBA\",\r\n  \"BlockId\": 52503607,\r\n  \"TxGroup\": \"3jehYoXLOACcweK6iTMsl2eS1coGIGVCylvggGxEyXo=\",\r\n  \"Timestamp\": \"2025-08-07T00:09:40+00:00\",\r\n  \"Protocol\": 1,\r\n  \"LiquidityProvider\": \"KQCO6GGB2P7BJ6IJZOXIYV7FDYA2ACXPZBFGKQIQWE4C23FCVEHJPTQJD4\",\r\n  \"PoolAddress\": \"IR6B7DJTTXWWWT63ZXU2V5DQTVFFKGFTBVGGYUNVHFUONWWPQEYL3QKGQQ\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"Y47M2UIOJETTPFV7OPKULDUVBYDIJFORFVIM6UN4MRGF47TJBHBA\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}