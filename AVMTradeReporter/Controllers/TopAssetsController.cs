using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/asset/top")]
    [Authorize]
    public class TopAssetsController : ControllerBase
    {
        private readonly ILogger<TopAssetsController> _logger;
        private readonly ITopAssetsService _topAssetsService;

        public TopAssetsController(ILogger<TopAssetsController> logger, ITopAssetsService topAssetsService)
        {
            _logger = logger;
            _topAssetsService = topAssetsService;
        }

        /// <summary>
        /// Get the "top assets" highlight lists for the scan homepage header: Popular (24h volume),
        /// Trending (1h volume), Top gainers/losers (24h price change in percent) and Top liquidity
        /// gainers/losers (24h real TVL change in percent). Candidates are the top 150 assets by real TVL excluding stable assets;
        /// each list holds up to 3 entries. The response is recomputed every 5 minutes and served from
        /// the Redis cache in between.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(TopAssetsResponse), 200)]
        public async Task<ActionResult<TopAssetsResponse>> GetTopAssets()
        {
            try
            {
                var response = await _topAssetsService.GetTopAssetsAsync(HttpContext.RequestAborted);
                // Client/proxy-side caching shaves off repeated hits inside the 5 minute recompute window.
                Response.Headers["Cache-Control"] = "public,max-age=60";
                return Ok(response);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { error = "Request canceled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get top assets");
                return StatusCode(500, new { error = "Failed to retrieve top assets" });
            }
        }
    }
}
