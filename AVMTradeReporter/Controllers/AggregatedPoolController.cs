using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/aggregated-pool")]
    public class AggregatedPoolController : ControllerBase
    {
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;
        private readonly IPoolRepository _poolRepository;
        private readonly ILogger<AggregatedPoolController> _logger;

        public AggregatedPoolController(AggregatedPoolRepository aggregatedPoolRepository, IPoolRepository poolRepository, ILogger<AggregatedPoolController> logger)
        {
            _aggregatedPoolRepository = aggregatedPoolRepository;
            _poolRepository = poolRepository;
            _logger = logger;
        }

        /// <summary>
        /// Get all aggregated pools or filter by asset ids. An asset id filter matches the asset on either side of the pair.
        /// </summary>
        /// <param name="assetIdA">Optional asset filter; matches pairs containing this asset on either side</param>
        /// <param name="assetIdB">Optional asset filter; matches pairs containing this asset on either side</param>
        /// <param name="offset">Number of pools to skip (default: 0)</param>
        /// <param name="size">Number of pools to return (default: 100)</param>
        /// <param name="orderBy">Server-side ordering (default: TVL) so that size/offset return the top items. Options: TVL, Volume1H, Volume24H, Volume7D, LastUpdated, PoolCount</param>
        /// <param name="direction">Sort direction (default: Desc)</param>
        /// <param name="light">When true, omits the level1Pools/level2Pools collections from the response to reduce payload size</param>
        /// <returns>List of aggregated pools</returns>
        [HttpGet]
        public ActionResult<List<AggregatedPool>> GetPools([FromQuery] ulong? assetIdA, [FromQuery] ulong? assetIdB, [FromQuery] int offset = 0, [FromQuery] int size = 100, [FromQuery] PoolOrderBy orderBy = PoolOrderBy.TVL, [FromQuery] SortDirection direction = SortDirection.Desc, [FromQuery] bool light = false)
        {
            try
            {
                var pools = _aggregatedPoolRepository.GetAllAggregatedPools(assetIdA, assetIdB, offset, size, orderBy, direction);
                if (light)
                {
                    pools = pools.Select(p => p.ToLightweight());
                }
                return Ok(pools.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools");
                return StatusCode(500, new { error = "Failed to retrieve pools" });
            }
        }

        /// <summary>
        /// Update and retrieve aggregated pool
        /// </summary>
        /// <returns>AggregatedPool</returns>
        [HttpGet("reload")]
        public async Task<ActionResult<AggregatedPool>> Reload([FromQuery] ulong assetIdA, [FromQuery] ulong assetIdB)
        {
            try
            {
                var cancellationToken = HttpContext.RequestAborted;
                await _poolRepository.UpdateAggregatedPool(assetIdA, assetIdB, cancellationToken);
                var pool = _aggregatedPoolRepository.GetAggregatedPool(assetIdA, assetIdB);
                return Ok(pool);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update or retrieve pool");
                return StatusCode(500, new { error = "Failed to update or retrieve pool" });
            }
        }
    }
}