using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// Provides DEX statistics endpoints for DefiLlama integration.
    /// All endpoints are publicly accessible (no authentication required).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly IStatsService _statsService;
        private readonly ILogger<StatsController> _logger;

        /// <param name="statsService">Service that aggregates DEX statistics.</param>
        /// <param name="logger">Logger instance.</param>
        public StatsController(IStatsService statsService, ILogger<StatsController> logger)
        {
            _statsService = statsService;
            _logger = logger;
        }

        /// <summary>
        /// Returns aggregated DEX statistics (volume, fees) for the given protocol over the window
        /// [<paramref name="timestamp"/>, <paramref name="to"/>). When <paramref name="to"/> is omitted,
        /// the window defaults to a full day: [timestamp, timestamp + 1 day), preserving the original
        /// daily-aggregate behaviour. An explicit <paramref name="to"/> allows arbitrary windows (e.g. 1
        /// hour) for consumers that pull data more granularly, such as DefiLlama's v2 adapter model
        /// (`pullHourly: true`).
        /// Only confirmed trades are included. Suitable for DefiLlama adapter consumption.
        /// </summary>
        /// <param name="dex">DEX protocol identifier: <c>Biatec</c>, <c>Pact</c>, or <c>Tiny</c>.</param>
        /// <param name="timestamp">Inclusive start of the statistics window.</param>
        /// <param name="to">Exclusive end of the statistics window. Defaults to <paramref name="timestamp"/> + 1 day when omitted.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// 200 with <see cref="DexStatsResponse"/> containing volume and fee totals.<br/>
        /// 400 when <paramref name="dex"/> is not a recognised protocol, or <paramref name="to"/> is not after <paramref name="timestamp"/>.
        /// </returns>
        [HttpGet("dex")]
        [ProducesResponseType(typeof(DexStatsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetDexStats(
            [FromQuery] string dex,
            [FromQuery] DateTimeOffset timestamp,
            [FromQuery] DateTimeOffset? to,
            CancellationToken ct)
        {
            if (!Enum.TryParse<DEXProtocol>(dex, ignoreCase: true, out var protocol))
            {
                return BadRequest(
                    $"Unknown DEX '{dex}'. Valid values: {string.Join(", ", Enum.GetNames<DEXProtocol>())}.");
            }

            if (to.HasValue && to.Value <= timestamp)
            {
                return BadRequest($"'to' ({to.Value:o}) must be after 'timestamp' ({timestamp:o}).");
            }

            try
            {
                var stats = await _statsService.GetDexStatsAsync(protocol, timestamp, to, ct);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error retrieving DEX stats for {Dex} at {Timestamp} to {To}", dex, timestamp, to);
                return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving DEX statistics.");
            }
        }
    }
}
