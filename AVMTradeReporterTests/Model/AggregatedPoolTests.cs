using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

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
            var resultEmpty = AggregatedPool.FromPools(Array.Empty<AVMTradeReporter.Models.Data.Pool>()).ToArray();
            Assert.That(resultEmpty.Length, Is.EqualTo(0));
        }

        [Test]
        public void FromPools_AggregatesAcrossBothDirections()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);

            var p1 = new AVMTradeReporter.Models.Data.Pool
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

            var p2 = new AVMTradeReporter.Models.Data.Pool
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
            var invalid = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 1,
                AssetIdB = 3,
                A = 1,
                B = 2
            };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Models.Data.Pool[] { p1, p2, invalid })
                .OrderBy(r => r.AssetIdA).ThenBy(r => r.AssetIdB).ToArray();

            // Assert two aggregated entries: (1,2) and (2,1)
            Assert.That(result.Length, Is.EqualTo(4));

            var agg12 = result.Single(r => r.AssetIdA == 1 && r.AssetIdB == 2);
            var agg21 = result.Single(r => r.AssetIdA == 2 && r.AssetIdB == 1);

            // Values are derived from virtual amounts (equal to real amounts for non-concentrated AMM)
            // For (1,2): A = p1.A_real (1.1) + reverse(p2).A_real (4.5) = 5.6
            //            B = p1.B_real (2.2) + reverse(p2).B_real (3.3) = 5.5
            Assert.That(agg12.VirtualSumALevel1, Is.EqualTo(8m));
            Assert.That(agg12.VirtualSumBLevel1, Is.EqualTo(6m));
            Assert.That(agg12.TVL_A, Is.EqualTo(8m));
            Assert.That(agg12.TVL_B, Is.EqualTo(6m));
            Assert.That(agg12.PoolCount, Is.EqualTo(2m));
            Assert.That(agg12.LastUpdated, Is.EqualTo(now)); // max timestamp
            Assert.That(agg12.Id, Is.EqualTo("1-2"));

            // For (2,1): A = p2.A_real (3.3) + reverse(p1).A_real (2.2) = 5.5
            //            B = p2.B_real (4.5) + reverse(p1).B_real (1.1) = 5.6
            Assert.That(agg21.VirtualSumALevel1, Is.EqualTo(6m));
            Assert.That(agg21.VirtualSumBLevel1, Is.EqualTo(8m));
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

            var p1 = new AVMTradeReporter.Models.Data.Pool
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
                AMMType = AMMType.OldAMM,
                Timestamp = older
            };

            var p2 = new AVMTradeReporter.Models.Data.Pool
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
                AMMType = AMMType.ConcentratedLiquidityAMM,
                Timestamp = now
            };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Models.Data.Pool[] { p1, p2 })
                .OrderBy(r => r.AssetIdA).ThenBy(r => r.AssetIdB).ToArray();

            // Assert two aggregated entries: (1,2) and (2,1)
            Assert.That(result.Length, Is.EqualTo(2));

            var agg12 = result.Single(r => r.AssetIdA == 1 && r.AssetIdB == 2);

            // Values are derived from virtual amounts (equal to real amounts for non-concentrated AMM)
            // For (1,2): A = p1.A_real (1.1) + reverse(p2).A_real (4.5) = 5.6
            //            B = p1.B_real (2.2) + reverse(p2).B_real (3.3) = 5.5
            Assert.That(agg12.VirtualSumALevel1, Is.EqualTo(33.411611892694053532345314914m));
            Assert.That(agg12.VirtualSumBLevel1, Is.EqualTo(45.523232618036247532345314914m));
            Assert.That(agg12.TVL_A, Is.EqualTo(9m));
            Assert.That(agg12.TVL_B, Is.EqualTo(11m));
            Assert.That(agg12.PoolCount, Is.EqualTo(2m));
            Assert.That(agg12.Id, Is.EqualTo("1-2"));

        }


        [Test]
        public void FromPools_Level2Calculation()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var older = now.AddMinutes(-1);

            var p1 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "addr-1-2",
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

            var p2 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "addr-2-1",
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
            var p3 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "addr-1-3",
                AssetIdA = 1,
                AssetADecimals = 6,
                AssetIdB = 3,
                AssetBDecimals = 6,
                A = 7_000_000,  // 1.0
                AF = 0,   // +0.1 -> 1.1
                B = 8_000_000,  // 2.0
                BF = 0,   // +0.2 -> 2.2
                Protocol = DEXProtocol.Pact,
                Timestamp = older
            };
            var p4 = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "addr-3-2",
                AssetIdA = 3,
                AssetADecimals = 6,
                AssetIdB = 2,
                AssetBDecimals = 6,
                A = 9_000_000,  // 1.0
                AF = 0,   // +0.1 -> 1.1
                B = 10_000_000,  // 2.0
                BF = 0,   // +0.2 -> 2.2
                Protocol = DEXProtocol.Pact,
                Timestamp = older
            };


            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Models.Data.Pool[] { p1, p2, p3, p4 })
                .OrderBy(r => r.AssetIdA).ThenBy(r => r.AssetIdB).ToArray();

            // Assert two aggregated entries: (1,2) and (2,1)
            Assert.That(result.Length, Is.EqualTo(6));

            var agg12 = result.Single(r => r.AssetIdA == 1 && r.AssetIdB == 2);

            Assert.That(agg12.VirtualSumALevel1, Is.EqualTo(8m));
            Assert.That(agg12.VirtualSumBLevel1, Is.EqualTo(6m));
            Assert.That(agg12.TVL_A, Is.EqualTo(8m));
            Assert.That(agg12.TVL_B, Is.EqualTo(6m));
            Assert.That(agg12.PoolCount, Is.EqualTo(2m));
            Assert.That(agg12.LastUpdated, Is.EqualTo(now)); // max timestamp
            Assert.That(agg12.Id, Is.EqualTo("1-2"));
            Assert.That(agg12.Level2Pools.ToArray(), Is.EqualTo(new string[] { p3.PoolAddress, p4.PoolAddress }));
            Assert.That(agg12.Level1Pools.ToArray(), Is.EqualTo(new string[] { p1.PoolAddress, p2.PoolAddress }));

            Assert.That(agg12.VirtualSumALevel2, Is.EqualTo(7));
            Assert.That(agg12.VirtualSumBLevel2, Is.EqualTo(8.888888888888888888888888889m));

        }
        [Test]
        public async Task GetAggregatedPoolVoteAlgo()
        {
            var pools = JsonConvert.DeserializeObject<AVMTradeReporter.Models.Data.Pool[]>(File.ReadAllText("Data/pools-vote-algo.json"));
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()));
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var repository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var cancellationTokenSource = new CancellationTokenSource();
            Assert.That(pools, Is.Not.Null, "Pools should not be null");
            foreach (var pool in pools)
            {
                await repository.StorePoolAsync(pool, false, cancellationTokenSource.Token);
            }

            await repository.UpdateAggregatedPool(pools[0].AssetIdA ?? 0, pools[0].AssetIdB ?? 0, cancellationTokenSource.Token);
            var aggregatedPool = aggregatedPoolsRepository.GetAggregatedPool(pools[0].AssetIdA ?? 0, pools[0].AssetIdB ?? 0);
            Assert.That(aggregatedPool, Is.Not.Null, "Aggregated pool should not be null");

            Assert.That(aggregatedPool.AssetIdA, Is.EqualTo(0));
            Assert.That(aggregatedPool.AssetIdB, Is.EqualTo(452399768));
            var price = aggregatedPool.VirtualSumBLevel1 / aggregatedPool.VirtualSumALevel1;
            Assert.That(price, Is.EqualTo(6.8129419971702496226889404405m));

        }

        [Test]
        public void FromPools_IgnoresPoolsMissingAmountsOrAssetIds()
        {
            // Arrange: only one valid pool, others invalid
            var valid = new AVMTradeReporter.Models.Data.Pool
            {
                AssetIdA = 10,
                AssetADecimals = 6,
                AssetIdB = 20,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 2_000_000
            };

            var missingA = new AVMTradeReporter.Models.Data.Pool { AssetIdA = 10, AssetIdB = 20, A = null, B = 1 };
            var missingB = new AVMTradeReporter.Models.Data.Pool { AssetIdA = 10, AssetIdB = 20, A = 1, B = null };
            var missingIds = new AVMTradeReporter.Models.Data.Pool { AssetIdA = null, AssetIdB = 20, A = 1, B = 1 };

            // Act
            var result = AggregatedPool.FromPools(new AVMTradeReporter.Models.Data.Pool[] { valid, missingA, missingB, missingIds }).ToArray();

            // With reverse union we expect two entries: (10,20) and (20,10)
            Assert.That(result.Length, Is.EqualTo(2));
            Assert.That(result.Any(r => r.AssetIdA == 10 && r.AssetIdB == 20), Is.True);
            Assert.That(result.Any(r => r.AssetIdA == 20 && r.AssetIdB == 10), Is.True);
        }
        [Test]
        public async Task GetAggregatedPoolAlgoUsdcLevel1()
        {
            var pools = JsonConvert.DeserializeObject<AVMTradeReporter.Models.Data.Pool[]>(File.ReadAllText("Data/pools-algo-usdc.json"));
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()));
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var repository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var cancellationTokenSource = new CancellationTokenSource();
            Assert.That(pools, Is.Not.Null, "Pools should not be null");
            foreach (var pool in pools)
            {
                await repository.StorePoolAsync(pool, false, cancellationTokenSource.Token);
            }

            await repository.UpdateAggregatedPool(0, 31566704, cancellationTokenSource.Token);
            var aggregatedPool = aggregatedPoolsRepository.GetAggregatedPool(0, 31566704);
            Assert.That(aggregatedPool, Is.Not.Null, "Aggregated pool should not be null");

            Assert.That(aggregatedPool.AssetIdA, Is.EqualTo(0));
            Assert.That(aggregatedPool.AssetIdB, Is.EqualTo(31566704));
            var price = aggregatedPool.VirtualSumBLevel1 / aggregatedPool.VirtualSumALevel1;
            Assert.That(price, Is.EqualTo(0.2419409725161350303906896744m));

        }

        [Test]
        public async Task GetAggregatedPoolAlgoUsdcLevel2()
        {
            var pools = JsonConvert.DeserializeObject<AVMTradeReporter.Models.Data.Pool[]>(File.ReadAllText("Data/pools-algo-usdc-big-20250820.json"));
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()));
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var repository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var cancellationTokenSource = new CancellationTokenSource();
            Assert.That(pools, Is.Not.Null, "Pools should not be null");
            foreach (var pool in pools)
            {
                await repository.StorePoolAsync(pool, false, cancellationTokenSource.Token);
            }

            await repository.UpdateAggregatedPool(0, 31566704, cancellationTokenSource.Token);
            var aggregatedPool = aggregatedPoolsRepository.GetAggregatedPool(0, 31566704);
            Assert.That(aggregatedPool, Is.Not.Null, "Aggregated pool should not be null");

            Assert.That(aggregatedPool.AssetIdA, Is.EqualTo(0));
            Assert.That(aggregatedPool.AssetIdB, Is.EqualTo(31566704));
            var priceLevel1 = aggregatedPool.VirtualSumBLevel1 / aggregatedPool.VirtualSumALevel1;
            Assert.That(priceLevel1, Is.EqualTo(0.2419287836457800044215208276m));
            var priceLevel2 = aggregatedPool.VirtualSumBLevel2 / aggregatedPool.VirtualSumALevel2;
            Assert.That(priceLevel2, Is.EqualTo(0.2248087775069365526107028402m));

        }
        [Test]
        public async Task Pool403797689()
        {
            var pool = JsonConvert.DeserializeObject<AVMTradeReporter.Models.Data.Pool>(File.ReadAllText("Data/pool-403797689.json"));
            var loggerPoolRepository = new LoggerFactory().CreateLogger<PoolRepository>();
            var loggerAggregatedPoolRepository = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregatedPoolsRepository = new AggregatedPoolRepository(null!, loggerAggregatedPoolRepository, null!, Options.Create(new AppConfiguration()));
            var config = new AppConfiguration() { };
            var options = new OptionsWrapper<AppConfiguration>(config);
            var repository = new PoolRepository(null!, loggerPoolRepository, null!, aggregatedPoolsRepository, options, null!, null!);
            var cancellationTokenSource = new CancellationTokenSource();
            Assert.That(pool, Is.Not.Null, "Pools should not be null");
            await repository.StorePoolAsync(pool, false, cancellationTokenSource.Token);
            await repository.UpdateAggregatedPool(403797689, 31566704, cancellationTokenSource.Token);
            var aggregatedPool = aggregatedPoolsRepository.GetAggregatedPool(403797689, 31566704);
            Assert.That(aggregatedPool, Is.Not.Null, "Aggregated pool should not be null");

            Assert.That(aggregatedPool.AssetIdA, Is.EqualTo(31566704));
            Assert.That(aggregatedPool.AssetIdB, Is.EqualTo(403797689));

        }

    }
}
