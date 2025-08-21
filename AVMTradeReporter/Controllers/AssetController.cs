using AVMTradeReporter.Processors.Image;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/asset")]
    public class AssetController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;

        public AssetController(ILogger<AssetController> logger)
        {
            _logger = logger;
        }

        /// Returns image for asset by id
        [HttpGet("image/{assetId}")]
        public async Task<IActionResult> GetAssetImage(ulong assetId)
        {
            try
            {
                var cancellationToken = HttpContext.RequestAborted;

                var processor = new MainnetImageProcessor();
                var data = await processor.LoadImageAsync(assetId, cancellationToken);
                if (data.Length > 100)
                {
                    // add cache headers to cache for 1 week
                    Response.Headers["Cache-Control"] = "public,max-age=604800"; // 1
                    Response.Headers["Expires"] = DateTime.UtcNow.AddDays(7).ToString("R");
                }
                return File(data, "image/png");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Upstream HTTP error retrieving asset image for id {AssetId}", assetId);
                return StatusCode(502, new { error = "Failed to retrieve asset image from upstream" });
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Request canceled while retrieving asset image for id {AssetId}", assetId);
                return StatusCode(499, new { error = "Request canceled" }); // 499 Client Closed Request (non-standard)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve asset image for id {AssetId}", assetId);
                return StatusCode(500, new { error = "Failed to retrieve asset image" });
            }
        }
    }
}
