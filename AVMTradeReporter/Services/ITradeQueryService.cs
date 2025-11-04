using AVMTradeReporter.Model.Data;
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
    }
}