using Algorand.Algod.Model;
using AVMTradeReporter.Repository;
using System.Collections.Concurrent;

namespace AVMTradeReporterTests
{
    public class MockAssetRepository : IAssetRepository
    {
        private readonly ConcurrentDictionary<ulong, Asset> _assets = new();

        public Task<Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            if (_assets.TryGetValue(assetId, out var a)) return Task.FromResult<Asset?>(a);
            var asset = new Asset
            {
                Index = assetId,
                Params = new AssetParams
                {
                    Total = 1000000,
                    Decimals = 6,
                    DefaultFrozen = false,
                    UnitName = "M" + assetId,
                    Name = "Mock Asset",
                    Url = "https://mock.asset",
                    MetadataHash = null,
                    Manager = null,
                    Reserve = null,
                    Freeze = null,
                    Clawback = null
                }
            };
            _assets[assetId] = asset;
            return Task.FromResult<Asset?>(asset);
        }

        public Task SetAssetAsync(Asset asset, CancellationToken cancellationToken = default)
        {
            _assets[asset.Index] = asset;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Asset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int size, CancellationToken cancellationToken)
        {
            IEnumerable<Asset> query = _assets.Values;
            if (ids != null && ids.Any())
            {
                query = query.Where(a => ids.Contains(a.Index));
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToLowerInvariant();
                query = query.Where(a => (a.Params?.Name?.ToLowerInvariant().Contains(s) ?? false) || (a.Params?.UnitName?.ToLowerInvariant().Contains(s) ?? false));
            }
            return Task.FromResult(query.Take(size));
        }
    }
}
