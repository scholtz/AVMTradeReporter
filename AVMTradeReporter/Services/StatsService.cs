using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Aggregates DEX trading statistics from Elasticsearch and maps them to
    /// <see cref="DexStatsResponse"/> for DefiLlama export.
    /// </summary>
    public class StatsService : IStatsService
    {
        private readonly IStatsRepository _statsRepository;
        private readonly ILogger<StatsService> _logger;

        /// <param name="statsRepository">Repository that executes Elasticsearch aggregation queries.</param>
        /// <param name="logger">Logger instance.</param>
        public StatsService(IStatsRepository statsRepository, ILogger<StatsService> logger)
        {
            _statsRepository = statsRepository;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<DexStatsResponse> GetDexStatsAsync(
            DEXProtocol protocol,
            DateTimeOffset from,
            CancellationToken cancellationToken = default)
        {
            var to = from.AddDays(1);

            try
            {
                var (volumeUSD, feesUSD, feesUSDProvider, feesUSDProtocol) =
                    await _statsRepository.GetDexAggregationsAsync(protocol.ToString(), from, to, cancellationToken);

                return new DexStatsResponse
                {
                    Protocol = protocol.ToString(),
                    From = from,
                    To = to,
                    VolumeUSD = (decimal)volumeUSD,
                    FeesUSD = (decimal)feesUSD,
                    FeesLPUSD = (decimal)feesUSDProvider,
                    FeesProtocolUSD = (decimal)feesUSDProtocol
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve DEX stats for {Protocol} [{From} – {To}]", protocol, from, to);

                return new DexStatsResponse
                {
                    Protocol = protocol.ToString(),
                    From = from,
                    To = to
                };
            }
        }
    }
}
