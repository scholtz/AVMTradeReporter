using Algorand.Algod;
using Algorand.KMD;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AVMTradeReporter.Repository
{
    public class AssetRepository : IAssetRepository
    {
        private readonly IDefaultApi _algod;
        private readonly ILogger<AssetRepository> _logger;
        private readonly IDatabase? _redisDatabase;
        public AssetRepository(
            IDefaultApi algod,
            ILogger<AssetRepository> logger,
            IDatabase? redisDatabase = null)
        {
            _algod = algod;
            _logger = logger;
            _redisDatabase = redisDatabase;
        }

        public async Task<Algorand.Algod.Model.Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (assetId == 0)
                {
                    return new Algorand.Algod.Model.Asset()
                    {
                        Index = 0,
                        Params = new Algorand.Algod.Model.AssetParams()
                        {
                            Total = 10_000_000_000,
                            Decimals = 6,
                            DefaultFrozen = false,
                            UnitName = "ALGO",
                            Name = "Algorand",
                            Url = "https://www.algorand.com",
                            MetadataHash = null,
                            Manager = null,
                            Reserve = null,
                            Freeze = null,
                            Clawback = null
                        }
                    };
                }

                var redisKey = $"asset:{assetId}";
                if (_redisDatabase != null)
                {
                    // Check Redis cache first
                    var cachedAsset = await _redisDatabase.StringGetAsync(redisKey);
                    if (cachedAsset.HasValue)
                    {
                        return System.Text.Json.JsonSerializer.Deserialize<Algorand.Algod.Model.Asset>(cachedAsset!);
                    }
                }

                var asset = await _algod.GetAssetByIDAsync(cancellationToken, assetId);
                if (_redisDatabase != null)
                {
                    var poolJson = System.Text.Json.JsonSerializer.Serialize(asset);
                    await _redisDatabase.StringSetAsync(redisKey, poolJson);
                }
                return asset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve asset {AssetId}", assetId);
                return null;
            }
        }
    }
}
