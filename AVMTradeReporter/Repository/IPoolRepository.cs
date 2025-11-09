using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Repository
{
    public interface IPoolRepository
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken);
        Task<bool> StorePoolAsync(Pool pool, bool updateAggregated = true, CancellationToken? cancellationToken = null);
        Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken);
        Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken);
        Task<List<Pool>> GetPoolsAsync(ulong? assetIdA, ulong? assetIdB, string? address, DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default);
        Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default);
        IPoolProcessor? GetPoolProcessor(DEXProtocol protocol);
        Task UpdateAggregatedPool(ulong aId, ulong bId, CancellationToken cancellationToken);
    }
}