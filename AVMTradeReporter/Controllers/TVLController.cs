using AVMTradeReporter.Model.DTO.TVL;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// Durable TVL (real total value locked, USD) OHLC history for a single asset, backed by the
    /// "tvlohlc" Elasticsearch index (see <see cref="TvlOhlcRepository"/>) - the TVL counterpart of
    /// <see cref="OHLCController"/>'s price history. Publicly accessible (no authentication
    /// required), same as OHLCController: this is public market data.
    ///
    /// Deliberately not a full TradingView UDF datafeed (no symbol resolution/config/search
    /// endpoints) - this is a plain bars endpoint for a future liquidity chart consumer to build on,
    /// not a drop-in TradingView data source.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class TVLController : ControllerBase
    {
        private readonly TvlOhlcRepository _tvlOhlcRepository;

        public TVLController(TvlOhlcRepository tvlOhlcRepository)
        {
            _tvlOhlcRepository = tvlOhlcRepository;
        }

        /// <summary>
        /// Returns hourly TVL (USD) bars for one asset over the trailing window ending now.
        /// </summary>
        /// <param name="assetId">Asset id (0 = ALGO).</param>
        /// <param name="hoursBack">How many trailing hours of bars to return (default 168 = 7 days).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] ulong assetId, [FromQuery] int hoursBack = 168, CancellationToken ct = default)
        {
            var candles = await _tvlOhlcRepository.GetCandlesAsync(assetId, DateTimeOffset.UtcNow, Math.Clamp(hoursBack, 1, 24 * 90), ct);
            var res = new TvlHistoryResponseDto
            {
                T = candles.T,
                O = candles.O,
                H = candles.H,
                L = candles.L,
                C = candles.C
            };
            return Ok(res);
        }
    }
}
