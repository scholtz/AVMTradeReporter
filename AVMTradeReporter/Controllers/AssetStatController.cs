using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/asset-stat")]
    public class AssetStatController : ControllerBase
    {
        private readonly IAssetStatRepository _assetStatRepository;
        private readonly ILogger<AssetStatController> _logger;

        public AssetStatController(IAssetStatRepository assetStatRepository, ILogger<AssetStatController> logger)
        {
            _assetStatRepository = assetStatRepository;
            _logger = logger;
        }

        /// <summary>
        /// Get backend-computed per-asset TVL/volume/fees/APR stats, optionally filtered by protocol.
        /// </summary>
        /// <param name="protocol">Optional protocol filter (Pact, Tiny, Biatec). When omitted, both the
        /// combined ("all protocols") rows and the per-protocol rows are returned.</param>
        /// <param name="sortBy">Optional sort field: TVLUSD, Volume24hUSD, Volume7dUSD, Apr24h, Apr7d. Defaults to TVLUSD.</param>
        /// <param name="direction">Sort direction (default: Desc)</param>
        /// <returns>List of asset stats</returns>
        [HttpGet]
        public ActionResult<List<AssetStat>> GetAssetStats(
            [FromQuery] DEXProtocol? protocol = null,
            [FromQuery] string? sortBy = "TVLUSD",
            [FromQuery] SortDirection direction = SortDirection.Desc)
        {
            try
            {
                var stats = _assetStatRepository.GetAllAsync(protocol, sortBy, direction == SortDirection.Desc);
                return Ok(stats.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get asset stats");
                return StatusCode(500, new { error = "Failed to retrieve asset stats" });
            }
        }

        /// <summary>
        /// Get the stat row for a single asset, optionally scoped to a protocol.
        /// </summary>
        /// <param name="assetId">Asset id to look up</param>
        /// <param name="protocol">Optional protocol filter; omit for the combined ("all protocols") row.</param>
        [HttpGet("{assetId}")]
        public ActionResult<AssetStat> GetAssetStat([FromRoute] ulong assetId, [FromQuery] DEXProtocol? protocol = null)
        {
            try
            {
                var stat = _assetStatRepository.GetByAssetIdAsync(assetId, protocol);
                if (stat == null) return NotFound();
                return Ok(stat);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get asset stat for {assetId}", assetId);
                return StatusCode(500, new { error = "Failed to retrieve asset stat" });
            }
        }
    }
}
