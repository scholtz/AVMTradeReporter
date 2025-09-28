using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Services
{
    public interface ILiquidityQueryService
    {
        Task<IEnumerable<Liquidity>> GetLiquidityAsync(
            ulong? assetIdA = null,
            ulong? assetIdB = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default);
    }
}