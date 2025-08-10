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

        [Test]
        public async Task PactSwapProcessorTestAlgo2Asa52545514()
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(52545514);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            Assert.That(dummyTradeService.trades.Count, Is.EqualTo(5));
            var json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdOut\": 386192725,\r\n  \"AssetAmountIn\": 66071089,\r\n  \"AssetAmountOut\": 14999,\r\n  \"TxId\": \"MIRJ3OH2PIRVINVQFJBJYHRIPJFZ5SZMF5FAY4LZWW6SUEON3SXA\",\r\n  \"BlockId\": 52545514,\r\n  \"TxGroup\": \"cuNUhExzJVTZVWL/RbaEarXEs5yKXrXSWWqU7CtGPi8=\",\r\n  \"Timestamp\": \"2025-08-08T09:22:44+00:00\",\r\n  \"Protocol\": 1,\r\n  \"Trader\": \"Q6WW3COZ5CIBLXF3F6ULOW3DTV3APR32FTSGGIXCSFIJRSDNFSU2P6PBP4\",\r\n  \"PoolAddress\": \"FKMV6GSU57764TQ5NLSBEDXNPUXL7OAOKANVDP2A6FSB77BHI3GTWHQAZA\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"MIRJ3OH2PIRVINVQFJBJYHRIPJFZ5SZMF5FAY4LZWW6SUEON3SXA\",\r\n  \"TradeState\": 1,\r\n  \"A\": 125808265,\r\n  \"B\": 552008095143,\r\n  \"BF\": 2156422\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(1).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdOut\": 386192725,\r\n  \"AssetAmountIn\": 110394855,\r\n  \"AssetAmountOut\": 25086,\r\n  \"TxId\": \"ARKJ5H5WF3AS232OKCRIPFPPSTP4RQADU3SYZC2OT3SJ6LR3SCVA\",\r\n  \"BlockId\": 52545514,\r\n  \"TxGroup\": \"0JlL/kP3jH7B/d6U5DozxZQMLMvqRPEKEdRh//8IopA=\",\r\n  \"Timestamp\": \"2025-08-08T09:22:44+00:00\",\r\n  \"Trader\": \"Q6WW3COZ5CIBLXF3F6ULOW3DTV3APR32FTSGGIXCSFIJRSDNFSU2P6PBP4\",\r\n  \"PoolAddress\": \"MUGDC5IFPGEJQLZWYTDTBKPZMWQS6ALNJ7RKQT5CYASSGMKBE6VETMEDK4\",\r\n  \"PoolAppId\": 661744776,\r\n  \"TopTxId\": \"ARKJ5H5WF3AS232OKCRIPFPPSTP4RQADU3SYZC2OT3SJ6LR3SCVA\",\r\n  \"TradeState\": 1,\r\n  \"A\": 212436959028,\r\n  \"B\": 48396851\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(2).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdOut\": 386192725,\r\n  \"AssetAmountIn\": 66236913,\r\n  \"AssetAmountOut\": 15051,\r\n  \"TxId\": \"DYECHLOE56OH2CFYJMSFRSK2YSN3JL5D2RDRDOHG5RDT3WNMQJTQ\",\r\n  \"BlockId\": 52545514,\r\n  \"TxGroup\": \"0afi4L2KLRsRbs/PeUEwySaS2hoJaEXdhg6mZ5hwuZU=\",\r\n  \"Timestamp\": \"2025-08-08T09:22:44+00:00\",\r\n  \"Trader\": \"Q6WW3COZ5CIBLXF3F6ULOW3DTV3APR32FTSGGIXCSFIJRSDNFSU2P6PBP4\",\r\n  \"PoolAddress\": \"QOPGIBA7TKZ7JGAVGZSL5PCSUOJUUMNUFAPHVARCLLO7NB64RQOCDCAXOY\",\r\n  \"PoolAppId\": 2757532390,\r\n  \"TopTxId\": \"DYECHLOE56OH2CFYJMSFRSK2YSN3JL5D2RDRDOHG5RDT3WNMQJTQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 88213552977,\r\n  \"B\": 20091825\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(3).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 2537013734,\r\n  \"AssetIdOut\": 31566704,\r\n  \"AssetAmountIn\": 330988365,\r\n  \"AssetAmountOut\": 90954561,\r\n  \"TxId\": \"ZRYWPQNCNVMBUAZF4PAKJNGA6Y4WD6SY2RMUBN7XSAKWDH5DH2OA\",\r\n  \"BlockId\": 52545514,\r\n  \"TxGroup\": \"rKFDwy1JycGSE3i+8HINpQHK0kc4kyaMf9mY/O5Vf58=\",\r\n  \"Timestamp\": \"2025-08-08T09:22:44+00:00\",\r\n  \"Protocol\": 1,\r\n  \"Trader\": \"Q6WW3COZ5CIBLXF3F6ULOW3DTV3APR32FTSGGIXCSFIJRSDNFSU2P6PBP4\",\r\n  \"PoolAddress\": \"VVBWRAJ3YSZGXBGJ2K654J3WA2VZOJW5U4SJCZWLM634H572WTGWTT53EE\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"AIBUOQAK4UVPMC3LHTEY5BFTUPPAUBX5BG5GCZM757K7HSOEWAEQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 1879663434160,\r\n  \"B\": 517988657518,\r\n  \"AF\": 32967353\r\n}"));
            json = Algorand.Utils.Encoder.EncodeToJson(dummyTradeService.trades.Skip(4).First());
            Assert.That(json, Is.EqualTo("{\r\n  \"AssetIdIn\": 2537013734,\r\n  \"AssetIdOut\": 31566704,\r\n  \"AssetAmountIn\": 52919651,\r\n  \"AssetAmountOut\": 14539186,\r\n  \"TxId\": \"WCDFMPLU3V7RLWXY5O4ORI2VTXD3CIT3TJCPBGCE77LD7RDYVDQA\",\r\n  \"BlockId\": 52545514,\r\n  \"TxGroup\": \"tu52LMXRcLK8uxx1padix2+XHUAZcQOZegI8r+nEPoE=\",\r\n  \"Timestamp\": \"2025-08-08T09:22:44+00:00\",\r\n  \"Protocol\": 1,\r\n  \"Trader\": \"Q6WW3COZ5CIBLXF3F6ULOW3DTV3APR32FTSGGIXCSFIJRSDNFSU2P6PBP4\",\r\n  \"PoolAddress\": \"VVBWRAJ3YSZGXBGJ2K654J3WA2VZOJW5U4SJCZWLM634H572WTGWTT53EE\",\r\n  \"PoolAppId\": 1002541853,\r\n  \"TopTxId\": \"N63LOJCRJBOKC6JZVEJLA7XQGJYK3223BN34Q2Q7P3B2YWBBV4DQ\",\r\n  \"TradeState\": 1,\r\n  \"A\": 1879716327352,\r\n  \"B\": 517974118332,\r\n  \"AF\": 32993812\r\n}"));
        }

    }
}