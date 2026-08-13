using AVMTradeReporter.Services;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Regression coverage for the Assets overview page showing a lower 24h volume for an asset than
    /// the sum of its individual pools' volumes (e.g. reported: $VOTE showed $4k total while the pool
    /// details page showed $VOTE/ALGO alone at $4k and $VOTE/USDC at $2k - the honest total should be
    /// at least $6k).
    ///
    /// Root cause: TradeQueryService.GetAssetVolumeSumsAsync (feeding TopAssetsService's
    /// SyncAssetVolumeCountersAsync, which writes BiatecAsset.Volume1H/24H/7D - the field the Assets
    /// table sorts and displays) credited each asset only HALF of every trade's ValueUSD, under the
    /// mistaken belief it needed to undo double-counting the way the aggregated-pool cache does (that
    /// cache stores each pair twice, as (A,B) and (B,A), so summing it does need /2 - see
    /// AggregatedPoolAssetVolumeConsistencyTests). This method has no such double storage: a trade's
    /// AssetIdIn and AssetIdOut are different fields holding different asset ids, so a given asset is
    /// bucketed by exactly ONE of the two Elasticsearch terms aggregations per trade - there was
    /// nothing to divide by 2. GetPoolVolumesAsync (which feeds the pool details page) credits each
    /// pool the FULL ValueUSD per trade, so halving it at the asset level made every asset's total
    /// systematically half of the true sum of its pools' volumes.
    /// </summary>
    public class AssetVolumeAggregationTests
    {
        private const ulong VOTE = 452399768UL;
        private const ulong ALGO = 0UL;
        private const ulong USDC = 31566704UL;

        [Test]
        public void MergeAssetVolumeBuckets_CreditsFullTradeValue_ToBothSides()
        {
            // One $100 trade: VOTE -> ALGO. It lands in the "byAssetIn" bucket for VOTE and the
            // "byAssetOut" bucket for ALGO - never both buckets for the same asset.
            var byAssetIn = new Dictionary<ulong, decimal> { [VOTE] = 100m };
            var byAssetOut = new Dictionary<ulong, decimal> { [ALGO] = 100m };

            var result = TradeQueryService.MergeAssetVolumeBuckets(byAssetIn, byAssetOut);

            Assert.That(result[VOTE], Is.EqualTo(100m), "VOTE's side of a $100 trade must count as $100, not $50");
            Assert.That(result[ALGO], Is.EqualTo(100m));
        }

        [Test]
        public void MergeAssetVolumeBuckets_AssetsTotal_MatchesSumOfItsPoolsVolumes()
        {
            // Matches the reported bug exactly: $VOTE/ALGO pool has $4k volume (VOTE was AssetIdIn on
            // those trades), $VOTE/USDC pool has $2k volume (VOTE was AssetIdOut on those trades).
            // The asset-level total for VOTE must be the honest sum: $6k.
            var byAssetIn = new Dictionary<ulong, decimal> { [VOTE] = 4000m };
            var byAssetOut = new Dictionary<ulong, decimal> { [ALGO] = 4000m, [VOTE] = 2000m, [USDC] = 2000m };

            var result = TradeQueryService.MergeAssetVolumeBuckets(byAssetIn, byAssetOut);

            Assert.That(result[VOTE], Is.EqualTo(6000m),
                "VOTE's total volume must equal the sum of its pools' volumes ($4k + $2k), not half of it");
        }
    }
}
