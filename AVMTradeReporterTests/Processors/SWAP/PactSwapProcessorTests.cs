using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class PactSwapProcessorTests
    {

        [Test]
        public async Task PactSwapProcessorTestAlgo2Asa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52531655);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyTradeService.trades.Count, Is.EqualTo(2));
            //var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            //Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 31566704,\r\n  \"AssetIdOut\": 1241945177,\r\n  \"AssetAmountIn\": 10000000,\r\n  \"AssetAmountOut\": 11057638,\r\n  \"TxId\": \"IVGSGTGVJ453X7TUKLGUNJNT7N4TYTAKJZBVZG2ZTKANLMUNVEGQ\",\r\n  \"BlockId\": 52531655,\r\n  \"TxGroup\": \"xVoaswyYIil6ogy3/68YfA3uT5P8OQGTZ1kC/I62BtY=\",\r\n  \"Timestamp\": \"2025-08-07T22:27:27+00:00\",\r\n  \"Protocol\": 2,\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"LBLOB5IO3UZX3JOBY2MHDLPEWWIKMPYIISAKLABX4B7S4SPG2W65OQXOKQ\",\r\n  \"PoolAppId\": 3098469455,\r\n  \"TopTxId\": \"4LHW76RBO4DMY5CWH5PXMTBJOU4SG3QRHMOZ42KFGU4EAW5QCDZQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 340363198271,\r\n  \"B\": 9651035455,\r\n  \"L\": 1476915374276447\r\n}"));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(1).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 1241945177,\r\n  \"AssetIdOut\": 31566704,\r\n  \"AssetAmountIn\": 11057638,\r\n  \"AssetAmountOut\": 10024195,\r\n  \"TxId\": \"QSWXTJL7KNG5227UG2GTCRJNEDDFMFYXEQ2JE2JH6BO7GSWKRPCQ\",\r\n  \"BlockId\": 52531655,\r\n  \"TxGroup\": \"xVoaswyYIil6ogy3/68YfA3uT5P8OQGTZ1kC/I62BtY=\",\r\n  \"Timestamp\": \"2025-08-07T22:27:27+00:00\",\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"IVNCBYPN4YSEQARE3BSYID64DIB2GD2XQD47ANYZXFBSHCASOLTZOQLKAQ\",\r\n  \"PoolAppId\": 1243421154,\r\n  \"TopTxId\": \"4LHW76RBO4DMY5CWH5PXMTBJOU4SG3QRHMOZ42KFGU4EAW5QCDZQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 5724617427,\r\n  \"B\": 6322692739\r\n}"));
        }
    }
}