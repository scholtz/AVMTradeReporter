using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Repository
{
    public interface IAssetRepository
    {
        public Task<Algorand.Algod.Model.Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default);

    }
}