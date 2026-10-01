using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Repository
{
    public interface IAssetRepository
    {
        Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default);
        Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default);
        Task<IEnumerable<BiatecAsset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int offset, int size, CancellationToken cancellationToken);

        /// <summary>
        /// True when the asset is known to be destroyed / non-existent on chain (a cached tombstone). <see cref="GetAssetAsync"/>
        /// returns null for that AND for a transient failure (algod timeout); callers that must tell the two apart ask here.
        /// </summary>
        Task<bool> IsDeletedAsync(ulong assetId, CancellationToken cancellationToken = default);
    }
}