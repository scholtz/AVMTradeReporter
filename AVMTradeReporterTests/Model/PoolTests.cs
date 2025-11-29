using System;
using System.Collections.Generic;
using System.Linq;
using AVMTradeReporter.Models.Data.Enums;
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

            var pool = new AVMTradeReporter.Models.Data.Pool
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
            var pool = JsonConvert.DeserializeObject<AVMTradeReporter.Models.Data.Pool>(File.ReadAllText("Data/pool-3136517663.json"));
            Assert.That(pool, Is.Not.Null, "Failed to deserialize pool data from JSON file.");
            Assert.That(pool.VirtualAmountA, Is.EqualTo(1423509.4775349025526735010167m));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(9698934.902364655801809186706m));
        }

        [Test]
        public void StableSwapTest_WhenRealAmountA_IsLessThan_RealAmountB()
        {
            // Arrange - StableSwap pool where A < B
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                StableA = 1_000_000,  // 1.0 (RealAmountA for non-Biatec)
                A = 0,
                AF = 0,
                StableB = 2_000_000,  // 2.0 (RealAmountB for non-Biatec)
                B = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.StableSwap,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            var virtualA = pool.VirtualAmountA;
            var virtualB = pool.VirtualAmountB;

            // Assert
            // Both virtual amounts should equal the minimum of the real amounts (1.0)
            Assert.That(virtualA, Is.EqualTo(1.0m), "VirtualAmountA should equal minimum of real amounts");
            Assert.That(virtualB, Is.EqualTo(1.0m), "VirtualAmountB should equal minimum of real amounts");
            Assert.That(virtualA, Is.EqualTo(virtualB), "VirtualAmountA and VirtualAmountB should be equal for StableSwap");

            // Verify the price is 1:1
            var price = virtualB / virtualA;
            Assert.That(price, Is.EqualTo(1.0m), "StableSwap pool should maintain 1:1 price ratio");
        }

        [Test]
        public void StableSwapTest_WhenRealAmountB_IsLessThan_RealAmountA()
        {
            // Arrange - StableSwap pool where B < A
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                StableA = 5_000_000,  // 5.0 (RealAmountA for non-Biatec)
                A = 0,
                AF = 0,
                StableB = 3_000_000,  // 3.0 (RealAmountB for non-Biatec)
                B = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.StableSwap,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            var virtualA = pool.VirtualAmountA;
            var virtualB = pool.VirtualAmountB;

            // Assert
            // Both virtual amounts should equal the minimum of the real amounts (3.0)
            Assert.That(virtualA, Is.EqualTo(3.0m), "VirtualAmountA should equal minimum of real amounts");
            Assert.That(virtualB, Is.EqualTo(3.0m), "VirtualAmountB should equal minimum of real amounts");
            Assert.That(virtualA, Is.EqualTo(virtualB), "VirtualAmountA and VirtualAmountB should be equal for StableSwap");

            // Verify the price is 1:1
            var price = virtualB / virtualA;
            Assert.That(price, Is.EqualTo(1.0m), "StableSwap pool should maintain 1:1 price ratio");
        }

        [Test]
        public void StableSwapTest_WithEqualAmounts()
        {
            // Arrange - StableSwap pool where A == B
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                StableA = 10_000_000,  // 10.0 (RealAmountA for non-Biatec)
                A = 0,
                AF = 0,
                StableB = 10_000_000,  // 10.0 (RealAmountB for non-Biatec)
                B = 0,
                BF = 0,
                Protocol = DEXProtocol.Tiny,
                AMMType = AMMType.StableSwap,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            var virtualA = pool.VirtualAmountA;
            var virtualB = pool.VirtualAmountB;

            // Assert
            // Both virtual amounts should equal 10.0
            Assert.That(virtualA, Is.EqualTo(10.0m), "VirtualAmountA should equal minimum of real amounts");
            Assert.That(virtualB, Is.EqualTo(10.0m), "VirtualAmountB should equal minimum of real amounts");
            Assert.That(virtualA, Is.EqualTo(virtualB), "VirtualAmountA and VirtualAmountB should be equal for StableSwap");

            // Verify the price is 1:1
            var price = virtualB / virtualA;
            Assert.That(price, Is.EqualTo(1.0m), "StableSwap pool should maintain 1:1 price ratio");
        }


        [Test]
        public void StableSwapTest_DifferentDecimals()
        {
            // Arrange - StableSwap pool with different decimal places
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 8,  // 8 decimals
                AssetIdB = 2,
                AssetBDecimals = 2,  // 2 decimals
                StableA = 500_000_000,     // 5.0 with 8 decimals
                A = 0,
                AF = 0,
                StableB = 700,             // 7.0 with 2 decimals
                B = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.StableSwap,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            var virtualA = pool.VirtualAmountA;
            var virtualB = pool.VirtualAmountB;

            // Assert
            // RealAmountA = 5.0, RealAmountB = 7.0, so both virtual amounts should be 5.0
            Assert.That(virtualA, Is.EqualTo(5.0m), "VirtualAmountA should equal minimum of real amounts");
            Assert.That(virtualB, Is.EqualTo(5.0m), "VirtualAmountB should equal minimum of real amounts");
            Assert.That(virtualA, Is.EqualTo(virtualB), "VirtualAmountA and VirtualAmountB should be equal for StableSwap");

            // Verify the price is 1:1
            var price = virtualB / virtualA;
            Assert.That(price, Is.EqualTo(1.0m), "StableSwap pool should maintain 1:1 price ratio");
        }

        [Test]
        public void StableSwapTest_ReversePool()
        {
            // Arrange - Create a StableSwap pool and reverse it
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                StableA = 15_000_000,  // 15.0
                A = 0,
                AF = 0,
                StableB = 10_000_000,  // 10.0
                B = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.StableSwap,
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            var reversedPool = pool.Reverse();

            // Assert - Original pool
            Assert.That(pool.VirtualAmountA, Is.EqualTo(10.0m), "Original pool VirtualAmountA should be minimum");
            Assert.That(pool.VirtualAmountB, Is.EqualTo(10.0m), "Original pool VirtualAmountB should be minimum");

            // Assert - Reversed pool should also maintain 1:1 ratio
            Assert.That(reversedPool.VirtualAmountA, Is.EqualTo(10.0m), "Reversed pool VirtualAmountA should be minimum");
            Assert.That(reversedPool.VirtualAmountB, Is.EqualTo(10.0m), "Reversed pool VirtualAmountB should be minimum");

            // Verify assets are swapped
            Assert.That(reversedPool.AssetIdA, Is.EqualTo(pool.AssetIdB), "Assets should be swapped in reversed pool");
            Assert.That(reversedPool.AssetIdB, Is.EqualTo(pool.AssetIdA), "Assets should be swapped in reversed pool");
        }
    }
}
