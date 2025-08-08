using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Repository
{
    public interface IPoolRepository
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken);
        Task<bool> StorePoolAsync(Pool pool, CancellationToken cancellationToken);
        Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken);
        Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken);
        Task<List<Pool>> GetPoolsAsync(DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default);
        Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default);
    }
}