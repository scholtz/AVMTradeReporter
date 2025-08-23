using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Repository
{
    public interface IAssetRepository
    {
        Task<Algorand.Algod.Model.Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default);
        Task SetAssetAsync(Algorand.Algod.Model.Asset asset, CancellationToken cancellationToken = default);
        Task<IEnumerable<Algorand.Algod.Model.Asset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int size, CancellationToken cancellationToken);
    }
}