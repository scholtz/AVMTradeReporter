using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class PactLiqudidityAddProcessorTests
    {

        [Test]
        public async Task PactLiqudidityAddProcessorTestAlgoAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52498683);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdB\": 2994233666,\r\n  \"AssetIdLP\": 3088984532,\r\n  \"AssetAmountA\": 40811179,\r\n  \"AssetAmountB\": 2239345972,\r\n  \"AssetAmountLP\": 290348031,\r\n  \"A\": 90198743950,\r\n  \"B\": 4949285941059,\r\n  \"L\": 641712110591,\r\n  \"TxId\": \"DMZRAFTJ3XHD4FPX6ILIRFROCSC3GQLI4XBWTMFF2XNNNUOFP2YQ\",\r\n  \"BlockId\": 52498683,\r\n  \"TxGroup\": \"O8tUjxVCxqit4mj4mhkKwvcrwFikHKk4GkClihAiN70=\",\r\n  \"Timestamp\": \"2025-08-06T20:15:04+00:00\",\r\n  \"LiquidityProvider\": \"3RCVHE4IMJVBWAGBYQJREIS675MZDQIAAVJSJR5R7TLXIPHDNUY3ZJMX6U\",\r\n  \"PoolAddress\": \"VIGNSWSMHPEO7ZC2SSBKAAALZV2SQOOTUEBWOYIX7NIJG7IIMZC7VOT2ZY\",\r\n  \"PoolAppId\": 3088984527,\r\n  \"TopTxId\": \"DMZRAFTJ3XHD4FPX6ILIRFROCSC3GQLI4XBWTMFF2XNNNUOFP2YQ\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}