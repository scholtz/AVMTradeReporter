using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/liquidity")]
    public class LiquidityController : ControllerBase
    {
        private readonly ILiquidityQueryService _liquidityQueryService;
        private readonly ILogger<LiquidityController> _logger;

        public LiquidityController(ILiquidityQueryService liquidityQueryService, ILogger<LiquidityController> logger)
        {
            _liquidityQueryService = liquidityQueryService;
            _logger = logger;
        }

        /// <summary>
        /// Get liquidity updates with optional filtering and pagination.
        /// </summary>
        /// <param name="assetIdA">Filter by asset A ID. When used alone, matches liquidity where this asset is either A or B.</param>
        /// <param name="assetIdB">Filter by asset B ID. When used with assetIdA, requires exact asset pair match.</param>
        /// <param name="txId">Filter by transaction ID. Takes precedence over asset filters.</param>
        /// <param name="offset">Number of records to skip for pagination (default: 0).</param>
        /// <param name="size">Maximum number of records to return (default: 100, max: 500).</param>
        /// <returns>List of liquidity updates matching the specified criteria.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<Liquidity>), 200)]
        public async Task<ActionResult<IEnumerable<Liquidity>>> GetLiquidity(
            [FromQuery] ulong? assetIdA = null,
            [FromQuery] ulong? assetIdB = null,
            [FromQuery] string? txId = null,
            [FromQuery] int offset = 0,
            [FromQuery] int size = 100)
        {
            try
            {
                // Validate and clamp size parameter
                size = Math.Clamp(size, 1, 500);

                var liquidityUpdates = await _liquidityQueryService.GetLiquidityAsync(
                    assetIdA,
                    assetIdB,
                    txId,
                    offset,
                    size,
                    HttpContext.RequestAborted);

                return Ok(liquidityUpdates);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { error = "Request canceled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get liquidity updates");
                return StatusCode(500, new { error = "Failed to get liquidity updates" });
            }
        }
    }
}