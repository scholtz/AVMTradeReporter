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
        /// Returns aggregated 24-hour DEX statistics (volume, fees) for the given protocol starting at
        /// <paramref name="timestamp"/>. The query window is [timestamp, timestamp + 1 day).
        /// Only confirmed trades are included. Suitable for DefiLlama adapter consumption.
        /// </summary>
        /// <param name="dex">DEX protocol identifier: <c>Biatec</c>, <c>Pact</c>, or <c>Tiny</c>.</param>
        /// <param name="timestamp">Inclusive start of the 24-hour statistics window.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// 200 with <see cref="DexStatsResponse"/> containing volume and fee totals.<br/>
        /// 400 when <paramref name="dex"/> is not a recognised protocol.
        /// </returns>
        [HttpGet("dex")]
        [ProducesResponseType(typeof(DexStatsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetDexStats(
            [FromQuery] string dex,
            [FromQuery] DateTimeOffset timestamp,
            CancellationToken ct)
        {
            if (!Enum.TryParse<DEXProtocol>(dex, ignoreCase: true, out var protocol))
            {
                return BadRequest(
                    $"Unknown DEX '{dex}'. Valid values: {string.Join(", ", Enum.GetNames<DEXProtocol>())}.");
            }

            try
            {
                var stats = await _statsService.GetDexStatsAsync(protocol, timestamp, ct);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error retrieving DEX stats for {Dex} at {Timestamp}", dex, timestamp);
                return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving DEX statistics.");
            }
        }
    }
}
