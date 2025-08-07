using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class BiatecLiqudidityRemoveProcessorTests
    {

        [Test]
        public async Task BiatecLiqudidityRemoveProcessorTestAsaAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52516075);
            
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"Direction\": 1,\r\n  \"AssetIdA\": 452399768,\r\n  \"AssetIdB\": 1241945177,\r\n  \"AssetIdLP\": 3136355103,\r\n  \"AssetAmountA\": 18432689,\r\n  \"AssetAmountB\": 1204051,\r\n  \"AssetAmountLP\": 204880897,\r\n  \"A\": 270603852314,\r\n  \"B\": 16918937288511,\r\n  \"L\": 11245285490516,\r\n  \"TxId\": \"SDXOVONWTYGWZIYLGWQOKKI7JV3ZPES6DHR6FCLW3Q3TO3FHZNXA\",\r\n  \"BlockId\": 52516075,\r\n  \"TxGroup\": \"jlpvx/jHYRU+Vp8Eb8dOXAS/VsFuuc2vdqcZJPixJDI=\",\r\n  \"Timestamp\": \"2025-08-07T10:06:13+00:00\",\r\n  \"LiquidityProvider\": \"SCHUF5FTBJ4FGA7EDCY2RJGSLUDOM6552DOBEBCLP3VMFW5QW74S3ZIEWA\",\r\n  \"PoolAddress\": \"VGQU27ZU64NUY5UIQPGXFVDCY2F7Y2LFW4KESTBFLCSBY7J53ZAA5UAXHQ\",\r\n  \"PoolAppId\": 3136355100,\r\n  \"TopTxId\": \"SDXOVONWTYGWZIYLGWQOKKI7JV3ZPES6DHR6FCLW3Q3TO3FHZNXA\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}