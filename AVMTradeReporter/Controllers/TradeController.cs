using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;
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
        /// <param name="assetId">Filter by asset ID on either side of the trade.</param>
        /// <param name="assetIdA">Filter unordered pair asset A.</param>
        /// <param name="assetIdB">Filter unordered pair asset B.</param>
        /// <param name="txId">Filter by transaction ID. Takes precedence over asset filters.</param>
        /// <param name="trader">Filter by trader address.</param>
        /// <param name="poolAddress">Filter by pool address.</param>
        /// <param name="poolAppId">Filter by pool application ID.</param>
        /// <param name="protocol">Filter by DEX protocol.</param>
        /// <param name="tradeState">Filter by trade state.</param>
        /// <param name="blockFrom">Filter by minimum block.</param>
        /// <param name="blockTo">Filter by maximum block.</param>
        /// <param name="timestampFrom">Filter by minimum timestamp.</param>
        /// <param name="timestampTo">Filter by maximum timestamp.</param>
        /// <param name="minValueUSD">Filter by minimum USD value.</param>
        /// <param name="maxValueUSD">Filter by maximum USD value.</param>
        /// <param name="minFeesUSD">Filter by minimum USD fees.</param>
        /// <param name="maxFeesUSD">Filter by maximum USD fees.</param>
        /// <param name="minAmountIn">Filter by minimum input amount.</param>
        /// <param name="maxAmountIn">Filter by maximum input amount.</param>
        /// <param name="minAmountOut">Filter by minimum output amount.</param>
        /// <param name="maxAmountOut">Filter by maximum output amount.</param>
        /// <param name="sortBy">Sort by timestamp, valueUSD, feesUSD, assetAmountIn, or assetAmountOut.</param>
        /// <param name="sortDirection">Sort direction: asc or desc.</param>
        /// <param name="offset">Number of records to skip for pagination (default: 0).</param>
        /// <param name="size">Maximum number of records to return (default: 100, max: 500).</param>
        /// <returns>List of trades or a paged result when advanced filters are used.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<Trade>), 200)]
        [ProducesResponseType(typeof(PagedResult<Trade>), 200)]
        public async Task<IActionResult> GetTrades(
            [FromQuery] ulong? assetIdIn = null,
            [FromQuery] ulong? assetIdOut = null,
            [FromQuery] ulong? assetId = null,
            [FromQuery] ulong? assetIdA = null,
            [FromQuery] ulong? assetIdB = null,
            [FromQuery] string? txId = null,
            [FromQuery] string? trader = null,
            [FromQuery] string? poolAddress = null,
            [FromQuery] ulong? poolAppId = null,
            [FromQuery] Models.Data.Enums.DEXProtocol? protocol = null,
            [FromQuery] Models.Data.Enums.TxState? tradeState = null,
            [FromQuery] ulong? blockFrom = null,
            [FromQuery] ulong? blockTo = null,
            [FromQuery] DateTimeOffset? timestampFrom = null,
            [FromQuery] DateTimeOffset? timestampTo = null,
            [FromQuery] decimal? minValueUSD = null,
            [FromQuery] decimal? maxValueUSD = null,
            [FromQuery] decimal? minFeesUSD = null,
            [FromQuery] decimal? maxFeesUSD = null,
            [FromQuery] ulong? minAmountIn = null,
            [FromQuery] ulong? maxAmountIn = null,
            [FromQuery] ulong? minAmountOut = null,
            [FromQuery] ulong? maxAmountOut = null,
            [FromQuery] string? sortBy = null,
            [FromQuery] string? sortDirection = null,
            [FromQuery] int offset = 0,
            [FromQuery] int size = 100)
        {
            try
            {
                offset = Math.Max(offset, 0);
                size = Math.Clamp(size, 1, 500);

                var filter = new TradeFilter
                {
                    AssetIdIn = assetIdIn,
                    AssetIdOut = assetIdOut,
                    AssetId = assetId,
                    AssetIdA = assetIdA,
                    AssetIdB = assetIdB,
                    TxId = txId,
                    Trader = trader,
                    PoolAddress = poolAddress,
                    PoolAppId = poolAppId,
                    Protocol = protocol,
                    TradeState = tradeState,
                    BlockFrom = blockFrom,
                    BlockTo = blockTo,
                    TimestampFrom = timestampFrom,
                    TimestampTo = timestampTo,
                    MinValueUSD = minValueUSD,
                    MaxValueUSD = maxValueUSD,
                    MinFeesUSD = minFeesUSD,
                    MaxFeesUSD = maxFeesUSD,
                    MinAmountIn = minAmountIn,
                    MaxAmountIn = maxAmountIn,
                    MinAmountOut = minAmountOut,
                    MaxAmountOut = maxAmountOut,
                    SortBy = sortBy,
                    SortDirection = sortDirection,
                    Offset = offset,
                    Size = size
                };

                if (filter.UsesAdvancedFilters)
                {
                    var pagedTrades = await _tradeQueryService.GetTradesAsync(filter, HttpContext.RequestAborted);
                    return Ok(pagedTrades);
                }

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