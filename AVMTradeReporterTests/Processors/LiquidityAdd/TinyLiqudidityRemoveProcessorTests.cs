using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class TinyLiqudidityRemoveProcessorTests
    {

        [Test]
        public async Task TinyLiqudidityRemoveProcessorTestAsaAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            //var block = await client.FetchBlockAsync(52492471);
            var block = await client.FetchBlockAsync(52491442);
            
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"Direction\": 1,\r\n  \"AssetIdA\": 2726252423,\r\n  \"AssetIdLP\": 2741164994,\r\n  \"AssetAmountA\": 87973280333,\r\n  \"AssetAmountLP\": 326728761709,\r\n  \"A\": 3414349123025,\r\n  \"B\": 154036522016,\r\n  \"TxId\": \"ZMNIR5BJN5XWAYLNIBJAVLOW3OYC6GLSMGCLHWHVFCOJSLPSTEKQ\",\r\n  \"BlockId\": 52491442,\r\n  \"TxGroup\": \"JUkDCmlwfXNt5q1nQhRTuI9Y+3iH8U8NtgqUZ2aYUMc=\",\r\n  \"Timestamp\": \"2025-08-06T14:29:04+00:00\",\r\n  \"Protocol\": 1,\r\n  \"LiquidityProvider\": \"KQCO6GGB2P7BJ6IJZOXIYV7FDYA2ACXPZBFGKQIQWE4C23FCVEHJPTQJD4\",\r\n  \"PoolAddress\": \"IR6B7DJTTXWWWT63ZXU2V5DQTVFFKGFTBVGGYUNVHFUONWWPQEYL3QKGQQ\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"ZMNIR5BJN5XWAYLNIBJAVLOW3OYC6GLSMGCLHWHVFCOJSLPSTEKQ\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}