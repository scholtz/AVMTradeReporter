using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Computes backend-side per-asset TVL/volume/fees/APR rollups so that clients no longer need to perform
    /// slow on-chain/aggregated-pool math themselves. See <see cref="AssetStat.ComputeApr"/> for the APR formula.
    /// </summary>
    public class AssetStatsService : IAssetStatsService
    {
        private readonly IPoolRepository _poolRepository;
        private readonly IAssetRepository _assetRepository;
        private readonly IAssetStatRepository _assetStatRepository;
        private readonly IStatsRepository _statsRepository;
        private readonly ILogger<AssetStatsService> _logger;

        public AssetStatsService(
            IPoolRepository poolRepository,
            IAssetRepository assetRepository,
            IAssetStatRepository assetStatRepository,
            IStatsRepository statsRepository,
            ILogger<AssetStatsService> logger)
        {
            _poolRepository = poolRepository;
            _assetRepository = assetRepository;
            _assetStatRepository = assetStatRepository;
            _statsRepository = statsRepository;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<List<AssetStat>> ComputeAssetStatsAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<AssetStat>();

            try
            {
                var now = DateTimeOffset.UtcNow;

                // Load every known pool once; the per-asset/per-protocol rollups below are all in-memory slices
                // of this same list, so we avoid re-querying the pool repository per asset.
                var allPools = await _poolRepository.GetPoolsAsync(
                    assetIdA: null,
                    assetIdB: null,
                    address: null,
                    protocol: null,
                    size: int.MaxValue,
                    orderBy: null,
                    direction: SortDirection.Desc,
                    cancellationToken: cancellationToken);

                if (allPools == null || allPools.Count == 0)
                {
                    _logger.LogDebug("No pools available; skipping asset stats computation");
                    return result;
                }

                // Fetch trailing fee sums per pool address once for the whole run (24h and 7d windows), reused
                // across every asset/protocol slice below instead of querying Elasticsearch per asset.
                var fees24hByPool = await _statsRepository.GetFeesUSDProviderByPoolAsync(now.AddHours(-24), now, cancellationToken);
                var fees7dByPool = await _statsRepository.GetFeesUSDProviderByPoolAsync(now.AddDays(-7), now, cancellationToken);

                var assetIds = allPools
                    .SelectMany(p => new[] { p.AssetIdA, p.AssetIdB })
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

                foreach (var assetId in assetIds)
                {
                    var relevantPools = allPools.Where(p => p.AssetIdA == assetId || p.AssetIdB == assetId).ToList();
                    if (relevantPools.Count == 0) continue;

                    var protocolScopes = new List<DEXProtocol?> { null };
                    protocolScopes.AddRange(relevantPools.Select(p => (DEXProtocol?)p.Protocol).Distinct());

                    BiatecAsset? asset = null;
                    try
                    {
                        asset = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load asset metadata for {assetId}", assetId);
                    }

                    foreach (var protocolScope in protocolScopes)
                    {
                        var poolsInScope = protocolScope.HasValue
                            ? relevantPools.Where(p => p.Protocol == protocolScope.Value).ToList()
                            : relevantPools;

                        if (poolsInScope.Count == 0) continue;

                        var tvlUsd = poolsInScope.Sum(p => (p.AssetIdA == assetId ? p.TotalTVLAssetAInUSD : p.TotalTVLAssetBInUSD) ?? 0m);
                        var volume24h = poolsInScope.Sum(p => p.Volume24H ?? 0m);
                        var volume7d = poolsInScope.Sum(p => p.Volume7D ?? 0m);

                        var poolAddresses = poolsInScope.Select(p => p.PoolAddress).Distinct();
                        var fees24h = poolAddresses.Sum(addr => fees24hByPool.TryGetValue(addr, out var f) ? f : 0m);
                        var fees7d = poolAddresses.Sum(addr => fees7dByPool.TryGetValue(addr, out var f) ? f : 0m);

                        var (apr24h, apr7d) = AssetStat.ComputeApr(tvlUsd, fees24h, fees7d);

                        var stat = new AssetStat
                        {
                            AssetId = assetId,
                            Protocol = protocolScope,
                            AssetName = asset?.Params?.Name,
                            UnitName = asset?.Params?.UnitName,
                            Decimals = (ulong?)asset?.Params?.Decimals,
                            ImageUrl = asset?.Params?.Url,
                            PriceUSD = asset?.PriceUSD,
                            TVLUSD = tvlUsd,
                            Volume24hUSD = volume24h,
                            Volume7dUSD = volume7d,
                            Fees24hUSD = fees24h,
                            Fees7dUSD = fees7d,
                            Apr24h = apr24h,
                            Apr7d = apr7d,
                            PoolCount = poolsInScope.Count,
                            LastUpdated = now
                        };

                        await _assetStatRepository.UpsertAsync(stat, cancellationToken);
                        result.Add(stat);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compute asset stats");
            }

            return result;
        }
    }
}
