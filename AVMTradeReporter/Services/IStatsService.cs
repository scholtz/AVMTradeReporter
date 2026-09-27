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
        /// Returns aggregated statistics for the given DEX protocol over the window
        /// [<paramref name="from"/>, <paramref name="to"/>). When <paramref name="to"/> is
        /// <see langword="null"/>, the window defaults to a full day: [from, from + 1 day).
        /// Only confirmed trades are included. Returns zeroed stats when data is unavailable.
        /// </summary>
        /// <param name="protocol">DEX protocol to aggregate.</param>
        /// <param name="from">Inclusive start of the window.</param>
        /// <param name="to">Exclusive end of the window. Defaults to <paramref name="from"/> + 1 day when <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<DexStatsResponse> GetDexStatsAsync(
            DEXProtocol protocol,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            CancellationToken cancellationToken = default);
    }
}
