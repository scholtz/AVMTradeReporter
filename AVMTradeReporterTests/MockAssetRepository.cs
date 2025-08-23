using Algorand.Algod.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests
{
    public class MockAssetRepository : IAssetRepository
    {
        private readonly Dictionary<ulong, Asset> _assets = new();

        public async Task<Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken);
            if (_assets.TryGetValue(assetId, out var a)) return a;
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
            return asset;
        }

        public Task SetAssetAsync(Asset asset, CancellationToken cancellationToken = default)
        {
            _assets[asset.Index] = asset;
            return Task.CompletedTask;
        }
    }
}
