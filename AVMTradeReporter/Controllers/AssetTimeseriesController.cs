using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/asset/timeseries")]
    [Authorize]
    public class AssetTimeseriesController : ControllerBase
    {
        private const int MaxAssetsPerRequest = 100;

        private readonly ILogger<AssetTimeseriesController> _logger;
        private readonly IAssetTimeseriesService _timeseriesService;

        public AssetTimeseriesController(ILogger<AssetTimeseriesController> logger, IAssetTimeseriesService timeseriesService)
        {
            _logger = logger;
            _timeseriesService = timeseriesService;
        }

        /// <summary>
        /// Get the 7 day hourly timeseries (USD price OHLC and real TVL OHLC candles) for up to 100
        /// assets at once, for the sparkline/candle columns on the asset and pool list pages. Series
        /// are recomputed on a 1 hour cadence and served from the Redis cache in between, so this
        /// endpoint is cheap to call for a whole page of assets.
        /// </summary>
        /// <param name="assetIds">Comma separated list of asset IDs (0 = ALGO). Max 100 per request.</param>
        [HttpGet("7d")]
        [ProducesResponseType(typeof(List<AssetTimeseries7D>), 200)]
        public async Task<ActionResult<List<AssetTimeseries7D>>> GetTimeseries7D([FromQuery] string assetIds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assetIds))
                {
                    return BadRequest(new { error = "assetIds is required" });
                }

                var ids = assetIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => ulong.TryParse(s, out var v) ? v : (ulong?)null)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .Distinct()
                    .Take(MaxAssetsPerRequest)
                    .ToList();
                if (ids.Count == 0)
                {
                    return BadRequest(new { error = "assetIds must contain at least one valid asset id" });
                }

                var response = await _timeseriesService.GetTimeseriesAsync(ids, HttpContext.RequestAborted);
                // Data only changes on the hourly recompute; let clients/proxies absorb repeat hits.
                Response.Headers["Cache-Control"] = "public,max-age=300";
                return Ok(response);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { error = "Request canceled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get 7d asset timeseries");
                return StatusCode(500, new { error = "Failed to retrieve asset timeseries" });
            }
        }
    }
}
