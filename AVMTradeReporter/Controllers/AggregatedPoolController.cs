using AVMTradeReporter.Model.Data;
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
        private readonly AggregatedPoolRepository _poolRepository;
        private readonly ILogger<AggregatedPoolController> _logger;

        public AggregatedPoolController(AggregatedPoolRepository poolRepository, ILogger<AggregatedPoolController> logger)
        {
            _poolRepository = poolRepository;
            _logger = logger;
        }

        /// <summary>
        /// Get all pools or filter by protocol
        /// </summary>
        /// <param name="protocol">Optional protocol filter (Pact, Tiny, Biatec)</param>
        /// <param name="size">Number of pools to return (default: 100)</param>
        /// <returns>List of pools</returns>
        [HttpGet]
        public ActionResult<List<AggregatedPool>> GetPools([FromQuery] ulong? assetIdA, [FromQuery] ulong? assetIdB, [FromQuery] int offset = 0, [FromQuery] int size = 100)
        {
            try
            {
                var pools = _poolRepository.GetAllAggregatedPools(assetIdA, assetIdB, offset, size);
                return Ok(pools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools");
                return StatusCode(500, new { error = "Failed to retrieve pools" });
            }
        }

    }
}