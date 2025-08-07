using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class PactLiqudidityRemoveProcessorTests
    {

        [Test]
        public async Task PactLiqudidityRemoveProcessorTestAlgoAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52500323);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"Direction\": 1,\r\n  \"AssetIdB\": 2994233666,\r\n  \"AssetIdLP\": 3087523751,\r\n  \"AssetAmountA\": 72615820,\r\n  \"AssetAmountB\": 3956421034,\r\n  \"AssetAmountLP\": 523845899,\r\n  \"A\": 6051025444,\r\n  \"B\": 329685790532,\r\n  \"L\": 43651711441,\r\n  \"TxId\": \"TPFBE6JIIQ64VOSO5F4XPSCDKHMMWFYB4IFDRVPDD6MN3HBVNCEQ\",\r\n  \"BlockId\": 52500323,\r\n  \"TxGroup\": \"Eza5V0Jyd+ipqxnitDBzExQbTWFtoBWSfDp+TBgyyeY=\",\r\n  \"Timestamp\": \"2025-08-06T21:33:41+00:00\",\r\n  \"LiquidityProvider\": \"OMSQCGWNPVPZGB2ANQ63REKLQJY53K2W2HMK2HGECP5MEUSQ2Z3FGM2L4Y\",\r\n  \"PoolAddress\": \"5MBDUD3X5I5BJL3X5GS2U4C6375YGXUEJMAN7TWDL24PGJFSD57AMCY2IM\",\r\n  \"PoolAppId\": 3087523746,\r\n  \"TopTxId\": \"TPFBE6JIIQ64VOSO5F4XPSCDKHMMWFYB4IFDRVPDD6MN3HBVNCEQ\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}