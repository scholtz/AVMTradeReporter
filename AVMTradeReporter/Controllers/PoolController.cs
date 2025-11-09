using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/pool")]
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
        public async Task<ActionResult<List<Pool>>> GetPools([FromQuery] ulong? assetIdA, [FromQuery] ulong? assetIdB, [FromQuery] string? address, [FromQuery] DEXProtocol? protocol = null, [FromQuery] int size = 100)
        {
            try
            {
                var pools = await _poolRepository.GetPoolsAsync(assetIdA, assetIdB, address, protocol, size, HttpContext.RequestAborted);
                return Ok(pools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools");
                return StatusCode(500, new { error = "Failed to retrieve pools" });
            }
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        /// <returns>Pool statistics</returns>
        [HttpGet("stats")]
        public async Task<ActionResult<object>> GetPoolStats([FromQuery] ulong? assetIdA, [FromQuery] ulong? assetIdB)
        {
            try
            {
                var totalCount = await _poolRepository.GetPoolCountAsync(HttpContext.RequestAborted);
                var allPools = await _poolRepository.GetPoolsAsync(assetIdA, assetIdB, null, size: int.MaxValue, cancellationToken: HttpContext.RequestAborted);

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
        /// <summary>
        /// Get pool statistics
        /// </summary>
        /// <returns>Pool statistics</returns>
        [HttpGet("reload")]
        public async Task<ActionResult<object>> Reload([FromQuery] DEXProtocol protocol, [FromQuery] ulong poolId, [FromQuery] string poolAddress)
        {
            try
            {
                var processor = _poolRepository.GetPoolProcessor(protocol);
                if (processor == null)
                {
                    throw new Exception("Processor not found");
                }
                return Ok(await processor.LoadPoolAsync(poolAddress, poolId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pool statistics");
                return StatusCode(500, new { error = "Failed to retrieve pool statistics" });
            }
        }
    }
}