using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/trade")]
    public class TradeController : ControllerBase
    {
        private readonly ITradeQueryService _tradeQueryService;
        private readonly ILogger<TradeController> _logger;

        public TradeController(ITradeQueryService tradeQueryService, ILogger<TradeController> logger)
        {
            _tradeQueryService = tradeQueryService;
            _logger = logger;
        }

        /// <summary>
        /// Get trades with optional filtering and pagination.
        /// </summary>
        /// <param name="assetIdIn">Filter by input asset ID. When used alone, matches trades where this asset is either input or output.</param>
        /// <param name="assetIdOut">Filter by output asset ID. When used with assetIdIn, requires exact asset pair match.</param>
        /// <param name="txId">Filter by transaction ID. Takes precedence over asset filters.</param>
        /// <param name="offset">Number of records to skip for pagination (default: 0).</param>
        /// <param name="size">Maximum number of records to return (default: 100, max: 500).</param>
        /// <returns>List of trades matching the specified criteria.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<Trade>), 200)]
        public async Task<ActionResult<IEnumerable<Trade>>> GetTrades(
            [FromQuery] ulong? assetIdIn = null,
            [FromQuery] ulong? assetIdOut = null,
            [FromQuery] string? txId = null,
            [FromQuery] int offset = 0,
            [FromQuery] int size = 100)
        {
            try
            {
                // Validate and clamp size parameter
                size = Math.Clamp(size, 1, 500);

                var trades = await _tradeQueryService.GetTradesAsync(
                    assetIdIn,
                    assetIdOut,
                    txId,
                    offset,
                    size,
                    HttpContext.RequestAborted);

                return Ok(trades);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { error = "Request canceled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get trades");
                return StatusCode(500, new { error = "Failed to get trades" });
            }
        }
    }
}