using System;
using System.Collections.Generic;
using System.Linq;
using AVMTradeReporter.Model.Data;

namespace AVMTradeReporterTests.Model
{
    public class AggregatedPoolTests
    {
        [Test]
        public void FromPools_ReturnsEmpty_ForNullOrEmpty()
        {
            // Null input
            var resultNull = AggregatedPool.FromPools(null!).ToArray();
            Assert.That(resultNull.Length, Is.EqualTo(0));

            // Empty input
            var resultEmpty = AggregatedPool.FromPools(Array.Empty<AVMTradeReporter.Model.Data.Pool>()).ToArray();
            Assert.That(resultEmpty.Length, Is.EqualTo(0));
        }

        [Test]
        public void FromPools_AggregatesAcrossBothDirections()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);

            var p1 = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                A = 4_000_000,  // 1.0
                AF = 0,   // +0.1 -> 1.1
                B = 3_000_000,  // 2.0
                BF = 0,   // +0.2 -> 2.2
                Protocol = DEXProtocol.Pact,
                Timestamp = older
            };

            var p2 = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 2,
                AssetADecimals = 6,
                AssetIdB = 1,
                AssetBDecimals = 6,
                A = 3_000_000,  // 3.0
                AF = 0,   // +0.3 -> 3.3
                B = 4_000_000,  // 4.0
                BF = 0,   // +0.5 -> 4.5
                Protocol = DEXProtocol.Tiny,
                Timestamp = now
            };

            // Include one invalid pool to ensure it's ignored
            var invalid = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 1,
                AssetIdB = 3,
                A = 1,
                B = 2
            };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Model.Data.Pool[] { p1, p2, invalid })
                .OrderBy(r => r.AssetIdA).ThenBy(r => r.AssetIdB).ToArray();

            // Assert two aggregated entries: (1,2) and (2,1)
            Assert.That(result.Length, Is.EqualTo(4));

            var agg12 = result.Single(r => r.AssetIdA == 1 && r.AssetIdB == 2);
            var agg21 = result.Single(r => r.AssetIdA == 2 && r.AssetIdB == 1);

            // Values are derived from virtual amounts (equal to real amounts for non-concentrated AMM)
            // For (1,2): A = p1.A_real (1.1) + reverse(p2).A_real (4.5) = 5.6
            //            B = p1.B_real (2.2) + reverse(p2).B_real (3.3) = 5.5
            Assert.That(agg12.A, Is.EqualTo(8m));
            Assert.That(agg12.B, Is.EqualTo(6m));
            Assert.That(agg12.TVL_A, Is.EqualTo(8m));
            Assert.That(agg12.TVL_B, Is.EqualTo(6m));
            Assert.That(agg12.PoolCount, Is.EqualTo(2m));
            Assert.That(agg12.LastUpdated, Is.EqualTo(now)); // max timestamp
            Assert.That(agg12.Id, Is.EqualTo("1-2"));

            // For (2,1): A = p2.A_real (3.3) + reverse(p1).A_real (2.2) = 5.5
            //            B = p2.B_real (4.5) + reverse(p1).B_real (1.1) = 5.6
            Assert.That(agg21.A, Is.EqualTo(6m));
            Assert.That(agg21.B, Is.EqualTo(8m));
            Assert.That(agg21.TVL_A, Is.EqualTo(6m));
            Assert.That(agg21.TVL_B, Is.EqualTo(8m));
            Assert.That(agg21.PoolCount, Is.EqualTo(2));
            Assert.That(agg21.LastUpdated, Is.EqualTo(now)); // max timestamp
            Assert.That(agg21.Id, Is.EqualTo("2-1"));
        }


        [Test]
        public void FromPools_AggregatesClAMMWithOldAMM()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);

            var p1 = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                A = 4_000_000,  // 1.0
                AF = 0,   // +0.1 -> 1.1
                B = 5_000_000,  // 2.0
                BF = 0,   // +0.2 -> 2.2
                Protocol = DEXProtocol.Pact,
                AMMType = AVMTradeReporter.Model.Data.AMMType.OldAMM,
                Timestamp = older
            };

            var p2 = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                PMin = 1,
                PMax = 2,
                A = 5_000_000_000,  // 3.0
                AF = 0,   // +0.3 -> 3.3
                B = 6_000_000_000,  // 4.0
                BF = 0,   // +0.5 -> 4.5
                Protocol = DEXProtocol.Biatec,
                AMMType = AVMTradeReporter.Model.Data.AMMType.ConcentratedLiquidityAMM,
                Timestamp = now
            };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Model.Data.Pool[] { p1, p2  })
                .OrderBy(r => r.AssetIdA).ThenBy(r => r.AssetIdB).ToArray();

            // Assert two aggregated entries: (1,2) and (2,1)
            Assert.That(result.Length, Is.EqualTo(2));

            var agg12 = result.Single(r => r.AssetIdA == 1 && r.AssetIdB == 2);

            // Values are derived from virtual amounts (equal to real amounts for non-concentrated AMM)
            // For (1,2): A = p1.A_real (1.1) + reverse(p2).A_real (4.5) = 5.6
            //            B = p1.B_real (2.2) + reverse(p2).B_real (3.3) = 5.5
            Assert.That(agg12.A, Is.EqualTo(33.411611892693684301049159175M));
            Assert.That(agg12.B, Is.EqualTo(45.523232618035869447454225347M));
            Assert.That(agg12.TVL_A, Is.EqualTo(9m));
            Assert.That(agg12.TVL_B, Is.EqualTo(11m));
            Assert.That(agg12.PoolCount, Is.EqualTo(2m));
            Assert.That(agg12.Id, Is.EqualTo("1-2"));

        }
        [Test]
        public void FromPools_IgnoresPoolsMissingAmountsOrAssetIds()
        {
            // Arrange: only one valid pool, others invalid
            var valid = new AVMTradeReporter.Model.Data.Pool
            {
                AssetIdA = 10,
                AssetADecimals = 6,
                AssetIdB = 20,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 2_000_000
            };

            var missingA = new AVMTradeReporter.Model.Data.Pool { AssetIdA = 10, AssetIdB = 20, A = null, B = 1 };
            var missingB = new AVMTradeReporter.Model.Data.Pool { AssetIdA = 10, AssetIdB = 20, A = 1, B = null };
            var missingIds = new AVMTradeReporter.Model.Data.Pool { AssetIdA = null, AssetIdB = 20, A = 1, B = 1 };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Model.Data.Pool[] { valid, missingA, missingB, missingIds }).ToArray();

            // With reverse union we expect two entries: (10,20) and (20,10)
            Assert.That(result.Length, Is.EqualTo(2));
            Assert.That(result.Any(r => r.AssetIdA == 10 && r.AssetIdB == 20), Is.True);
            Assert.That(result.Any(r => r.AssetIdA == 20 && r.AssetIdB == 10), Is.True);
        }
    }
}
