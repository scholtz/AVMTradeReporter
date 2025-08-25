using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AVMTradeReporter.Model.Data
{
    /// <summary>
    /// Aggregated view of liquidity for a specific asset pair across all matching pools.
    /// Sums A and B amounts for pools that share the same AssetIdA and AssetIdB.
    /// </summary>
    public class AggregatedPool
    {
        /// <summary>
        /// Identifier for the aggregated pool, formed by concatenating AssetIdA and AssetIdB.
        /// </summary>
        public string Id => $"{AssetIdA}-{AssetIdB}";
        /// <summary>
        /// Asset A id of the pair.
        /// </summary>
        public ulong AssetIdA { get; set; }

        /// <summary>
        /// Asset B id of the pair.
        /// </summary>
        public ulong AssetIdB { get; set; }

        /// <summary>
        /// Total aggregated amount of asset A across all pools with this pair form virtual pool amount.
        /// </summary>
        public decimal VirtualSumALevel1 { get; set; }

        /// <summary>
        /// Total aggregated amount of asset B across all pools with this pair.
        /// </summary>
        public decimal VirtualSumBLevel1 { get; set; }


        /// <summary>
        /// Total aggregated amount of asset A across all pools with single intermediary asset excluding the direct asset a to asset b pools which are at VirtualSumALevel1.
        /// </summary>
        public decimal VirtualSumALevel2 { get; set; }

        /// <summary>
        /// Total aggregated amount of asset B across all pools with single intermediary asset excluding the direct asset a to asset b pools which are at VirtualSumALevel1.
        /// </summary>
        public decimal VirtualSumBLevel2 { get; set; }
        /// <summary>
        /// Sum of locked asset A across all pools with this pair.
        /// </summary>
        public decimal TVL_A { get; set; }
        /// <summary>
        /// Sum of locked asset B across all pools with this pair.
        /// </summary>
        public decimal TVL_B { get; set; }

        /// <summary>
        /// Number of pools aggregated into this result.
        /// </summary>
        public int PoolCount { get; set; }
        /// <summary>
        /// List of direcr pools that were aggregated into this result.
        /// </summary>
        public SortedSet<string> Level1Pools { get; set; } = new SortedSet<string>();
        /// <summary>
        /// Gets or sets the collection of Level 2 pool identifiers.
        /// </summary>
        /// <remarks>The collection ensures that all identifiers are unique and automatically maintains
        /// them in sorted order.</remarks>
        public SortedSet<string> Level2Pools { get; set; } = new SortedSet<string>();


        /// <summary>
        /// Most recent timestamp among the aggregated pools (if any).
        /// </summary>
        public DateTimeOffset? LastUpdated { get; set; }
        /// <summary>
        /// Aggregated sum of virtual amounts of level 1 and level 2 pools for asset A.
        /// </summary>
#if RELEASE
        public decimal VirtualSumA => VirtualSumALevel1;
#else
        public decimal VirtualSumA => VirtualSumALevel1 + VirtualSumALevel2;
#endif
        /// <summary>
        /// Aggregated sum of virtual amounts of level 1 and level 2 pools for asset B.
        /// </summary>
#if RELEASE
        public decimal VirtualSumB => VirtualSumBLevel1;
#else
        public decimal VirtualSumB => VirtualSumBLevel1 + VirtualSumBLevel2;
#endif

        /// <summary>
        /// Total USD value for asset A side of this pool (RealAmountA * AssetA USD price). To be populated by services after asset prices are updated.
        /// </summary>
        public decimal? TotalTVLAssetAInUSD { get; set; }
        /// <summary>
        /// Total USD value for asset B side of this pool (RealAmountB * AssetB USD price). To be populated by services after asset prices are updated.
        /// </summary>
        public decimal? TotalTVLAssetBInUSD { get; set; }
        /// <summary>
        /// Aggregates pools by (AssetIdA, AssetIdB) and computes the sum of A and B.
        /// Pools missing asset ids or amounts are ignored or treated as zero respectively.
        /// </summary>
        /// <param name="pools">Sequence of pools to aggregate.</param>
        /// <returns>Aggregated pools grouped by asset pair.</returns>
        public static IEnumerable<AggregatedPool> FromPools(IEnumerable<Pool> pools, bool level1Only = false)
        {
            var ret = new SortedDictionary<string, AggregatedPool>();
            if (pools == null) return ret.Values;

            var grouped = pools
                .Where(p => p.AssetIdA.HasValue && p.AssetIdB.HasValue && p.A.HasValue && p.B.HasValue && p.AssetIdA != p.AssetIdB)
                .Union(pools.Select(p => p.Reverse()).Where(p => p.AssetIdA.HasValue && p.AssetIdB.HasValue && p.A.HasValue && p.B.HasValue))
                .GroupBy(p => new { A = p.AssetIdA!.Value, B = p.AssetIdB!.Value });

            foreach (var g in grouped)
            {
                //Console.WriteLine(JsonConvert.SerializeObject(g.Sum(p => p.VirtualAmountA)));
                //Console.WriteLine(JsonConvert.SerializeObject(g.Sum(p => p.VirtualAmountB)));
                //Console.WriteLine(JsonConvert.SerializeObject(g));
                ret[$"{g.Key.A}-{g.Key.B}"] = new AggregatedPool
                {
                    AssetIdA = g.Key.A,
                    AssetIdB = g.Key.B,
                    VirtualSumALevel1 = g.Sum(p => p.VirtualAmountA),
                    VirtualSumBLevel1 = g.Sum(p => p.VirtualAmountB),
                    TotalTVLAssetAInUSD = g.Sum(p => p.TotalTVLAssetAInUSD ?? 0),
                    TotalTVLAssetBInUSD = g.Sum(p => p.TotalTVLAssetBInUSD ?? 0),
                    TVL_A = g.Sum(p => p.RealAmountA),
                    TVL_B = g.Sum(p => p.RealAmountB),
                    PoolCount = g.Count(),
                    LastUpdated = g.Max(p => p.Timestamp),
                    Level1Pools = new SortedSet<string>(g.Select(p => p.PoolAddress)),
                };
                ret[$"{g.Key.B}-{g.Key.A}"] = ret[$"{g.Key.A}-{g.Key.B}"].Reverse();
            }
            //#if RELEASE
            //// temporarily disabled 
            //return ret.Values;
            //#endif
            if (level1Only)
            {
                // if only level 1 pools are requested, return them now
                return ret.Values;
            }


            // Add Level 2 pools
            // find intermediary pools for each aggregated pool
            // intermediary pool is the pool where exists aggreageted pool from asset A to asset C and from asset C to asset B

            var lowFeePools = pools.Where(p => p.LPFee < 0.001m).ToList();
            var tier1LowFeePools = FromPools(lowFeePools, true);

            foreach (var kv in ret)
            {
                var pool = ret[kv.Key];

                var intermediaryPools = tier1LowFeePools.Where(p =>
                    (p.AssetIdA == pool.AssetIdA || p.AssetIdA == pool.AssetIdB || p.AssetIdB == pool.AssetIdA || p.AssetIdB == pool.AssetIdB) &&
                    !((p.AssetIdA == pool.AssetIdA && p.AssetIdB == pool.AssetIdB) ||
                        (p.AssetIdA == pool.AssetIdB && p.AssetIdB == pool.AssetIdA))
                );

                // find asset C pools
                var intermediaryAssets = intermediaryPools.Select(k =>
                {
                    if (k.AssetIdA == pool.AssetIdA) return k.AssetIdB;
                    if (k.AssetIdA == pool.AssetIdB) return k.AssetIdB;
                    if (k.AssetIdB == pool.AssetIdA) return k.AssetIdA;
                    if (k.AssetIdB == pool.AssetIdB) return k.AssetIdA;
                    return ulong.MaxValue;
                }).Where(k => k != ulong.MaxValue).Distinct();

                foreach (var assetC in intermediaryAssets)
                {
                    // get minimum of the poosible swap
                    // find minimum of amount for intermediary asset and skew the second pool
                    var keyAC = $"{pool.AssetIdA}-{assetC}";
                    var keyCB = $"{assetC}-{pool.AssetIdB}";
                    if (ret.TryGetValue(keyAC, out var aggregatedPoolAC) && ret.TryGetValue(keyCB, out var aggregatedPoolCB))
                    {
                        if (aggregatedPoolAC.AssetIdA != pool.AssetIdA)
                        {
                            aggregatedPoolAC = aggregatedPoolAC.Reverse();
                        }
                        if (aggregatedPoolCB.AssetIdB != pool.AssetIdB)
                        {
                            aggregatedPoolCB = aggregatedPoolCB.Reverse();
                        }

                        var minAssetCVirtual = Math.Min(aggregatedPoolAC.VirtualSumBLevel1, aggregatedPoolCB.VirtualSumALevel1);
                        if (aggregatedPoolAC.VirtualSumBLevel1 > aggregatedPoolCB.VirtualSumALevel1 * 100) continue;
                        if (aggregatedPoolAC.VirtualSumALevel1 > aggregatedPoolCB.VirtualSumBLevel1 * 100) continue;
                        if (minAssetCVirtual > 0 && aggregatedPoolAC.VirtualSumBLevel1 > 0 && aggregatedPoolCB.VirtualSumALevel1 > 0)
                        {
                            var aggregatedPoolACAVirtual = aggregatedPoolAC.VirtualSumALevel1 * minAssetCVirtual / aggregatedPoolAC.VirtualSumBLevel1;
                            var aggregatedPoolCABBVirtual = aggregatedPoolCB.VirtualSumBLevel1 * minAssetCVirtual / aggregatedPoolCB.VirtualSumALevel1;

                            if (aggregatedPoolACAVirtual == 0) continue;
                            if (aggregatedPoolCABBVirtual == 0) continue;

                            pool.VirtualSumALevel2 += aggregatedPoolACAVirtual;
                            pool.VirtualSumBLevel2 += aggregatedPoolCABBVirtual;

                            var p = aggregatedPoolCABBVirtual / aggregatedPoolACAVirtual;
                            //if (pool.AssetIdA == 0 && pool.AssetIdB == 31566704)
                            //{

                            //}
                            foreach (var poolAddress in aggregatedPoolAC.Level1Pools)
                            {
                                pool.Level2Pools.Add(poolAddress + "|" + p);
                            }
                            foreach (var poolAddress in aggregatedPoolCB.Level1Pools)
                            {
                                pool.Level2Pools.Add(poolAddress + "|" + p);
                            }
                        }
                    }
                }
            }


            return ret.Values;
        }
        /// <summary>
        /// Reverse the asset pair in this aggregated pool.
        /// </summary>
        /// <returns></returns>
        public AggregatedPool Reverse()
        {
            return new AggregatedPool
            {
                AssetIdA = AssetIdB,
                AssetIdB = AssetIdA,
                VirtualSumALevel1 = VirtualSumBLevel1,
                VirtualSumBLevel1 = VirtualSumALevel1,
                VirtualSumALevel2 = VirtualSumBLevel2,
                VirtualSumBLevel2 = VirtualSumALevel2,
                TVL_A = TVL_B,
                TVL_B = TVL_A,
                PoolCount = PoolCount,
                LastUpdated = LastUpdated,
                Level1Pools = Level1Pools,
                Level2Pools = Level2Pools,
                TotalTVLAssetAInUSD = TotalTVLAssetBInUSD,
                TotalTVLAssetBInUSD = TotalTVLAssetAInUSD,
            };
        }
    }
}
