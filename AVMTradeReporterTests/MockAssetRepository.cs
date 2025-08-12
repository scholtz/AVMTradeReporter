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
        public async Task<Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken);
            return new Asset
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
        }
    }
}
