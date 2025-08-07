using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class BiatecLiqudidityAddProcessorTests
    {
        [Test]
        public async Task BiatecLiqudidityAddProcessorTestAlgoAsa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52514070);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyLiquidityService.list.Count, Is.EqualTo(1));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyLiquidityService.list.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdA\": 452399768,\r\n  \"AssetIdLP\": 3136517666,\r\n  \"AssetAmountA\": 73676000000,\r\n  \"AssetAmountB\": 10796000000,\r\n  \"AssetAmountLP\": 860186120363,\r\n  \"A\": 357388934582847,\r\n  \"B\": 31057600537663,\r\n  \"L\": 3165963192059952,\r\n  \"TxId\": \"XBIUYZI4D2CRCA2Z7AYIUUNJTFLNX7GQPIJAXAU5J6I5FBJ6AYMQ\",\r\n  \"BlockId\": 52514070,\r\n  \"TxGroup\": \"7IxOqaUPuv3yu5sr09fBbOTW1WhznAWBDj7zq3py/Rc=\",\r\n  \"Timestamp\": \"2025-08-07T08:30:58+00:00\",\r\n  \"LiquidityProvider\": \"SCHUF5FTBJ4FGA7EDCY2RJGSLUDOM6552DOBEBCLP3VMFW5QW74S3ZIEWA\",\r\n  \"PoolAddress\": \"RT5KAKAZZS7IPGTDXKP27LS7I2M5VBX336YA3VP4UKEDS2UVOWHPTKR5QE\",\r\n  \"PoolAppId\": 3136517663,\r\n  \"TopTxId\": \"XBIUYZI4D2CRCA2Z7AYIUUNJTFLNX7GQPIJAXAU5J6I5FBJ6AYMQ\",\r\n  \"TxState\": 1\r\n}"));
        }
    }
}