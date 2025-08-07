using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    public class TinySwapProcessorTests
    {

        [Test]
        public async Task TinySwapProcessorTestAlgo2Asa()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52530303);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyTradeService.trades.Count, Is.EqualTo(3));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdOut\": 3032713424,\r\n  \"AssetAmountIn\": 40000,\r\n  \"AssetAmountOut\": 761407020399,\r\n  \"TxId\": \"BA47LAIDFXE5E3IAK6IW63W3SC7B53W4FTHALZLTPT25EW4TWNTQ\",\r\n  \"BlockId\": 52530303,\r\n  \"TxGroup\": \"zCGIIKi51wlpDwnijA3ZDzj5yMnOYPJtkAiWUnng7ls=\",\r\n  \"Timestamp\": \"2025-08-07T21:23:31+00:00\",\r\n  \"Trader\": \"7VQ7TIO6UMIG3LTRGEKHCSCOUKRSZEWTKV4PPDRCTMO7ODSVIIU55PIH4I\",\r\n  \"PoolAddress\": \"2JPTIQWQEWFA6LIHCIXQ24LBDY7ZJ2DHKC3X5FPEITLMDB4C2BDIOXN6QE\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"BA47LAIDFXE5E3IAK6IW63W3SC7B53W4FTHALZLTPT25EW4TWNTQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 441027660972969186,\r\n  \"B\": 23099620951\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(1).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 1164556102,\r\n  \"AssetIdOut\": 760037151,\r\n  \"AssetAmountIn\": 564333,\r\n  \"AssetAmountOut\": 1763080,\r\n  \"TxId\": \"4T6WC4XW2IVOLIMOTAI6XXFJRAM7P7LHUIM3GV7ODYFR7XSEJLAQ\",\r\n  \"BlockId\": 52530303,\r\n  \"TxGroup\": \"Il36Z8mzQPADsKard0OxiAP2swtDf0htbtqJqlSKFQA=\",\r\n  \"Timestamp\": \"2025-08-07T21:23:31+00:00\",\r\n  \"Trader\": \"LY2HOSDHZDTHF6A5BYOVOLTHXVJT4UHRZA4F7TKYK5NSRDSLV4E3DQWGLA\",\r\n  \"PoolAddress\": \"KGYQ4EDXWXAGCUGQDLQ4MYSIPAS5KOR6MG7FOIB446Z5ZNNUYFEYDQISTA\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"BDOQ4AHWBAXH7SG35ICYN4EZDI4HCDYQ6CCSVEQWVYN4ZAWZLU3A\",\r\n  \"TradeState\": 1,\r\n  \"A\": 3821144028,\r\n  \"B\": 11972091586\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(2).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 760037151,\r\n  \"AssetAmountIn\": 1763080,\r\n  \"AssetAmountOut\": 6572399,\r\n  \"TxId\": \"BEXZIPDZEBRJOQC53LX3UECF3E7WQP2QUPZXMYUVL2KZUHNNFPVQ\",\r\n  \"BlockId\": 52530303,\r\n  \"TxGroup\": \"Il36Z8mzQPADsKard0OxiAP2swtDf0htbtqJqlSKFQA=\",\r\n  \"Timestamp\": \"2025-08-07T21:23:31+00:00\",\r\n  \"Trader\": \"LY2HOSDHZDTHF6A5BYOVOLTHXVJT4UHRZA4F7TKYK5NSRDSLV4E3DQWGLA\",\r\n  \"PoolAddress\": \"5U2V3U4RM5NXUBX7RNKHIPXSNXNB76PMK2DISR3BCLR366XQTGO7ZTZALA\",\r\n  \"PoolAppId\": 2967083464,\r\n  \"TopTxId\": \"FEJHOIK2XEYCNB6CFD3WN4VZHMJWUVF3Y64BCWWCZYPKD2SKIXBQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 34209732909,\r\n  \"B\": 9151163560\r\n}"));
        }
    }
}