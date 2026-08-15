using AVMTradeReporter.Repository;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Confirms the in-memory asset cache is populated. Program.cs blocks app.Run() on
    /// AssetRepository.EnsureInitializedAsync, so an empty cache after startup indicates the
    /// cache was wiped or never hydrated rather than "still loading".
    /// </summary>
    public class AssetCacheHealthCheck : IHealthCheck
    {
        private readonly IAssetRepository _assetRepository;

        public AssetCacheHealthCheck(IAssetRepository assetRepository)
        {
            _assetRepository = assetRepository;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var assets = await _assetRepository.GetAssetsAsync(null, null, 0, 1, cancellationToken);
            return assets.Any()
                ? HealthCheckResult.Healthy("Asset cache populated")
                : HealthCheckResult.Degraded("Asset cache empty");
        }
    }
}
