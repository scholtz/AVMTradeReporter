using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PoolController : ControllerBase
    {
        private readonly IPoolRepository _poolRepository;
        private readonly ILogger<PoolController> _logger;

        public PoolController(IPoolRepository poolRepository, ILogger<PoolController> logger)
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
        public async Task<ActionResult<List<Pool>>> GetPools([FromQuery] DEXProtocol? protocol = null, [FromQuery] int size = 100)
        {
            try
            {
                var pools = await _poolRepository.GetPoolsAsync(protocol, size, HttpContext.RequestAborted);
                return Ok(pools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools");
                return StatusCode(500, new { error = "Failed to retrieve pools" });
            }
        }

        /// <summary>
        /// Get a specific pool by address, app ID, and protocol
        /// </summary>
        /// <param name="poolAddress">Pool address</param>
        /// <param name="poolAppId">Pool app ID</param>
        /// <param name="protocol">Protocol (Pact, Tiny, Biatec)</param>
        /// <returns>Pool details if found</returns>
        [HttpGet("{poolAddress}/{poolAppId}/{protocol}")]
        public async Task<ActionResult<Pool>> GetPool(string poolAddress, ulong poolAppId, DEXProtocol protocol)
        {
            try
            {
                var pool = await _poolRepository.GetPoolAsync(poolAddress, poolAppId, protocol, HttpContext.RequestAborted);
                
                if (pool == null)
                {
                    return NotFound(new { error = "Pool not found" });
                }
                
                return Ok(pool);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pool {poolAddress}_{poolAppId}_{protocol}", poolAddress, poolAppId, protocol);
                return StatusCode(500, new { error = "Failed to retrieve pool" });
            }
        }

        /// <summary>
        /// Get pools by protocol
        /// </summary>
        /// <param name="protocol">Protocol (Pact, Tiny, Biatec)</param>
        /// <param name="size">Number of pools to return (default: 100)</param>
        /// <returns>List of pools for the specified protocol</returns>
        [HttpGet("by-protocol/{protocol}")]
        public async Task<ActionResult<List<Pool>>> GetPoolsByProtocol(DEXProtocol protocol, [FromQuery] int size = 100)
        {
            try
            {
                var pools = await _poolRepository.GetPoolsAsync(protocol, size, HttpContext.RequestAborted);
                return Ok(pools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools for protocol {protocol}", protocol);
                return StatusCode(500, new { error = "Failed to retrieve pools" });
            }
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        /// <returns>Pool statistics</returns>
        [HttpGet("stats")]
        public async Task<ActionResult<object>> GetPoolStats()
        {
            try
            {
                var totalCount = await _poolRepository.GetPoolCountAsync(HttpContext.RequestAborted);
                var allPools = await _poolRepository.GetPoolsAsync(size: int.MaxValue, cancellationToken: HttpContext.RequestAborted);
                
                var stats = new
                {
                    TotalPools = totalCount,
                    ProtocolStats = allPools
                        .GroupBy(p => p.Protocol)
                        .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                    LastUpdated = allPools
                        .Where(p => p.Timestamp.HasValue)
                        .Max(p => p.Timestamp)
                };

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pool statistics");
                return StatusCode(500, new { error = "Failed to retrieve pool statistics" });
            }
        }
    }
}