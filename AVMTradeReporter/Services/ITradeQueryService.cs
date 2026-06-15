using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Services
{
    public interface ITradeQueryService
    {
        Task<IEnumerable<Trade>> GetTradesAsync(
            ulong? assetIdIn = null,
            ulong? assetIdOut = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default);

        Task<PagedResult<Trade>> GetTradesAsync(TradeFilter filter, CancellationToken cancellationToken = default);

        Task<Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>> GetPoolVolumesAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken = default);
    }
}