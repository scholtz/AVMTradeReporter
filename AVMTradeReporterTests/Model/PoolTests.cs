using System;
using System.Collections.Generic;
using System.Linq;
using AVMTradeReporter.Model.Data.Enums;
using Newtonsoft.Json;

namespace AVMTradeReporterTests.Model
{
    public class PoolTests
    {

        [Test]
        public void ClAMMTest()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);

            var pool = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                PMin = 1,
                PMax = 2,
                A = 3_000_000_000,  // 3.0
                AF = 0,   // +0.3 -> 3.3
                B = 4_000_000_000,  // 4.0
                BF = 0,   // +0.5 -> 4.5
                Protocol = DEXProtocol.Biatec,
                AMMType = AMMType.ConcentratedLiquidityAMM,
                Timestamp = now
            };

            Assert.That(pool.VirtualAmountA, Is.EqualTo(18.401179052349389741062655345m));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(25.780556292368994838666032793m));

        }
        [Test]
        public void ClAMMTest3136517663()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);
            var pool = JsonConvert.DeserializeObject<AVMTradeReporter.Model.Data.Pool>(File.ReadAllText("Data/pool-3136517663.json"));
            Assert.That(pool, Is.Not.Null, "Failed to deserialize pool data from JSON file.");
            Assert.That(pool.VirtualAmountA, Is.EqualTo(1423509.4775349025526735010167m));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(9698934.902364655801809186706m));
        }
    }
}
