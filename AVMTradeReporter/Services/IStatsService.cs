using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Provides aggregated DEX trading statistics for DefiLlama export.
    /// </summary>
    public interface IStatsService
    {
        /// <summary>
        /// Returns aggregated statistics for the given DEX protocol over the 24-hour window
        /// starting at <paramref name="from"/>. The window is [from, from + 1 day).
        /// Only confirmed trades are included. Returns zeroed stats when data is unavailable.
        /// </summary>
        /// <param name="protocol">DEX protocol to aggregate.</param>
        /// <param name="from">Inclusive start of the 24-hour window.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<DexStatsResponse> GetDexStatsAsync(
            DEXProtocol protocol,
            DateTimeOffset from,
            CancellationToken cancellationToken = default);
    }
}
