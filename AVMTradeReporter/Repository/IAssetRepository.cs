using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Repository
{
    public interface IAssetRepository
    {
        Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default);
        Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default);
        Task<IEnumerable<BiatecAsset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int offset, int size, CancellationToken cancellationToken);
    }
}