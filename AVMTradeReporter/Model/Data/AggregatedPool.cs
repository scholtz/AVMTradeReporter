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
        public decimal A { get; set; }

        /// <summary>
        /// Total aggregated amount of asset B across all pools with this pair.
        /// </summary>
        public decimal B { get; set; }
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
        /// Most recent timestamp among the aggregated pools (if any).
        /// </summary>
        public DateTimeOffset? LastUpdated { get; set; }

        /// <summary>
        /// Aggregates pools by (AssetIdA, AssetIdB) and computes the sum of A and B.
        /// Pools missing asset ids or amounts are ignored or treated as zero respectively.
        /// </summary>
        /// <param name="pools">Sequence of pools to aggregate.</param>
        /// <returns>Aggregated pools grouped by asset pair.</returns>
        public static IEnumerable<AggregatedPool> FromPools(IEnumerable<Pool> pools)
        {
            if (pools == null) yield break;

            var grouped = pools
                .Where(p => p.AssetIdA.HasValue && p.AssetIdB.HasValue && p.A.HasValue && p.B.HasValue)
                .Union(pools.Select(p => p.Reverse()).Where(p => p.AssetIdA.HasValue && p.AssetIdB.HasValue && p.A.HasValue && p.B.HasValue))
                .GroupBy(p => new { A = p.AssetIdA!.Value, B = p.AssetIdB!.Value });

            foreach (var g in grouped)
            {
                Console.WriteLine(JsonConvert.SerializeObject(g.Sum(p => p.VirtualAmountA)));
                Console.WriteLine(JsonConvert.SerializeObject(g.Sum(p => p.VirtualAmountB)));
                Console.WriteLine(JsonConvert.SerializeObject(g));
                yield return new AggregatedPool
                {
                    AssetIdA = g.Key.A,
                    AssetIdB = g.Key.B,
                    A = g.Sum(p => p.VirtualAmountA),
                    B = g.Sum(p => p.VirtualAmountB),
                    TVL_A = g.Sum(p => p.RealAmountA),
                    TVL_B = g.Sum(p => p.RealAmountB),
                    PoolCount = g.Count(),
                    LastUpdated = g.Max(p => p.Timestamp)
                };
            }
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
                A = B,
                B = A,
                TVL_A = TVL_B,
                TVL_B = TVL_A,
                PoolCount = PoolCount,
                LastUpdated = LastUpdated
            };
        }
    }
}
