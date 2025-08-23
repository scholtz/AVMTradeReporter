using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Repository
{
    public interface IAssetRepository
    {
        Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default);
        Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default);
    }
}