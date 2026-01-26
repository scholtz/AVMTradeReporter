using Algorand.Algod.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using System.Collections.Concurrent;

namespace AVMTradeReporterTests
{
    public class MockAssetRepository : IAssetRepository
    {
        private readonly ConcurrentDictionary<ulong, BiatecAsset> _assets = new();

        public Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            if (_assets.TryGetValue(assetId, out var a)) return Task.FromResult<BiatecAsset?>(a);
            var asset = new BiatecAsset
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
                },
                PriceUSD = 0,
                TVL_USD = 0,
                StabilityIndex = 0
            };
            _assets[assetId] = asset;
            return Task.FromResult<BiatecAsset?>(asset);
        }

        public Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default)
        {
            _assets[asset.Index] = asset;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<BiatecAsset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int offset, int size, CancellationToken cancellationToken)
        {
            IEnumerable<BiatecAsset> query = _assets.Values;
            if (ids != null && ids.Any())
            {
                query = query.Where(a => ids.Contains(a.Index));
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToLowerInvariant();
                if (s == "utility")
                {
                    query = query.Where(a => a.StabilityIndex == 0);
                }
                if (s == "stable")
                {
                    query = query.Where(a => a.StabilityIndex > 0);
                }
                else
                {
                    query = query.Where(a => (a.Params?.Name?.ToLowerInvariant().Contains(s) ?? false) || (a.Params?.UnitName?.ToLowerInvariant().Contains(s) ?? false));
                }
            }
            return Task.FromResult(query.Skip(offset).Take(size));
        }
    }
}
