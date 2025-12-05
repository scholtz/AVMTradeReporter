using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class BiatecSwapProcessorTests
    {

        [Test]
        public async Task BiatecSwapProcessorTestAsa2Asa()
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
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 31566704,\r\n  \"AssetIdOut\": 1241945177,\r\n  \"AssetAmountIn\": 10000000,\r\n  \"AssetAmountOut\": 11057638,\r\n  \"TxId\": \"IVGSGTGVJ453X7TUKLGUNJNT7N4TYTAKJZBVZG2ZTKANLMUNVEGQ\",\r\n  \"BlockId\": 52531655,\r\n  \"TxGroup\": \"xVoaswyYIil6ogy3/68YfA3uT5P8OQGTZ1kC/I62BtY=\",\r\n  \"Timestamp\": \"2025-08-07T22:27:27+00:00\",\r\n  \"Protocol\": 2,\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"LBLOB5IO3UZX3JOBY2MHDLPEWWIKMPYIISAKLABX4B7S4SPG2W65OQXOKQ\",\r\n  \"PoolAppId\": 3098469455,\r\n  \"TopTxId\": \"4LHW76RBO4DMY5CWH5PXMTBJOU4SG3QRHMOZ42KFGU4EAW5QCDZQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 87931850122939,\r\n  \"B\": 3070341160354,\r\n  \"L\": 1688528423018591\r\n}"));
            //json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(1).First());
            //Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 1241945177,\r\n  \"AssetIdOut\": 31566704,\r\n  \"AssetAmountIn\": 11057638,\r\n  \"AssetAmountOut\": 10024195,\r\n  \"TxId\": \"QSWXTJL7KNG5227UG2GTCRJNEDDFMFYXEQ2JE2JH6BO7GSWKRPCQ\",\r\n  \"BlockId\": 52531655,\r\n  \"TxGroup\": \"xVoaswyYIil6ogy3/68YfA3uT5P8OQGTZ1kC/I62BtY=\",\r\n  \"Timestamp\": \"2025-08-07T22:27:27+00:00\",\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"IVNCBYPN4YSEQARE3BSYID64DIB2GD2XQD47ANYZXFBSHCASOLTZOQLKAQ\",\r\n  \"PoolAppId\": 1243421154,\r\n  \"TopTxId\": \"4LHW76RBO4DMY5CWH5PXMTBJOU4SG3QRHMOZ42KFGU4EAW5QCDZQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 5724617427,\r\n  \"B\": 6322692739\r\n}"));
        }
        [Test]
        public async Task BiatecSwapProcessorTestAsa2Asa55991837()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(55991837);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyTradeService.trades.Count, Is.EqualTo(2));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 452399768,\r\n  \"AssetAmountIn\": 300000000,\r\n  \"AssetAmountOut\": 45534675,\r\n  \"TxId\": \"RZW5OEVFYDAEHZYEBVVTTNE56ROGT7PY3W3IN7BSJI5265DK27JA\",\r\n  \"BlockId\": 55991837,\r\n  \"TxGroup\": \"HCfDK2dLnh0k2PZG0BOybvHanTJ806//Zdt90RvNK5M=\",\r\n  \"Timestamp\": \"2025-11-28T20:45:18+00:00\",\r\n  \"Protocol\": 2,\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"RT5KAKAZZS7IPGTDXKP27LS7I2M5VBX336YA3VP4UKEDS2UVOWHPTKR5QE\",\r\n  \"PoolAppId\": 3136517663,\r\n  \"TopTxId\": \"M4D72LUUIPEGFJU2TATQZ4PEPISJHSQ6CKY7MRD34J2PWYAOIMBA\",\r\n  \"TradeState\": 1,\r\n  \"A\": 620652196491000,\r\n  \"B\": 143764374536000,\r\n  \"L\": 9308923171977128\r\n}"));
            //json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(1).First());
            //Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 1241945177,\r\n  \"AssetIdOut\": 31566704,\r\n  \"AssetAmountIn\": 11057638,\r\n  \"AssetAmountOut\": 10024195,\r\n  \"TxId\": \"QSWXTJL7KNG5227UG2GTCRJNEDDFMFYXEQ2JE2JH6BO7GSWKRPCQ\",\r\n  \"BlockId\": 52531655,\r\n  \"TxGroup\": \"xVoaswyYIil6ogy3/68YfA3uT5P8OQGTZ1kC/I62BtY=\",\r\n  \"Timestamp\": \"2025-08-07T22:27:27+00:00\",\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"IVNCBYPN4YSEQARE3BSYID64DIB2GD2XQD47ANYZXFBSHCASOLTZOQLKAQ\",\r\n  \"PoolAppId\": 1243421154,\r\n  \"TopTxId\": \"4LHW76RBO4DMY5CWH5PXMTBJOU4SG3QRHMOZ42KFGU4EAW5QCDZQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 5724617427,\r\n  \"B\": 6322692739\r\n}"));
        }
        [Test]
        public async Task BiatecSwapProcessorTestAsa2Asa56210430()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(56210430);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyTradeService.trades.Count, Is.EqualTo(2));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 452399768,\r\n  \"AssetAmountIn\": 300000000,\r\n  \"AssetAmountOut\": 45651760,\r\n  \"TxId\": \"CEYG5VLZSW7GD4ZCLZHQTKZH5RVRX5WSEZFPNO3U6DH2XK5FHQIQ\",\r\n  \"BlockId\": 56210430,\r\n  \"TxGroup\": \"LgYJgL3NKYWql2QbwSUeUL1uChHCo+wvHWeUOfyHewc=\",\r\n  \"Timestamp\": \"2025-12-05T22:01:38+00:00\",\r\n  \"Protocol\": 2,\r\n  \"Trader\": \"RFJOKPEZ3ER3WOU3LR4FXMIT4BPBTDTBBKV4PH34UCDX4RIH6PTMDM3UJY\",\r\n  \"PoolAddress\": \"RT5KAKAZZS7IPGTDXKP27LS7I2M5VBX336YA3VP4UKEDS2UVOWHPTKR5QE\",\r\n  \"PoolAppId\": 3136517663,\r\n  \"TopTxId\": \"JWRC3V4JYLQ2D5FOLUGLTINEDOG4W4WE7EBEY7ZWQLKHWVPF3UXQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 590703432028057,\r\n  \"B\": 148603080142993,\r\n  \"L\": 9320135279849240\r\n}"));
        }
    }
}