using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Processors.Image;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/asset")]
    public class AssetController : ControllerBase
    {
        private const string NativeAlgoLogoSvg = """
<?xml version="1.0" encoding="UTF-8"?>
<svg width="198px" height="198px" viewBox="0 0 198 198" version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
    <title>algorand-logomark-blue-RGB</title>
    <g id="Page-1" stroke="none" strokeWidth="1" fill="none" fill-rule="evenodd">
        <g id="algorand-logomark-blue-RGB" transform="translate(0.3, 0)" fill="#2D2DF1" fill-rule="nonzero">
            <polygon id="Path" points="197.2 197.6 166.4 197.6 146.3 123 103.1 197.6 68.6 197.6 135.3 82.1 124.5 41.8 34.5 197.6 2.84217094e-14 197.6 114.1 0 144.4 0 157.5 49.2 188.7 49.2 167.5 86.2"></polygon>
        </g>
    </g>
</svg>
""";

        private readonly ILogger<AssetController> _logger;
        private readonly IAssetRepository _assetRepository;

        public AssetController(ILogger<AssetController> logger, IAssetRepository assetRepository)
        {
            _logger = logger;
            _assetRepository = assetRepository;
        }

        /// <summary>
        /// List assets from the in-memory cache (prefilled from Redis) or filter by IDs / search term.
        /// </summary>
        /// <param name="ids">Comma separated list of asset IDs to include. Missing IDs will be fetched on-demand.</param>
        /// <param name="search">Case-insensitive substring filter applied to asset name or unit name. Special case: utility returns utility tokens. Special case: stable returns the assets with stabilityIndex > 0.</param>
        /// <param name="size">Maximum number of results to return (default 100, max 500).</param>
        /// <returns>List of matching assets with basic metadata.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<BiatecAsset>), 200)]
        public async Task<ActionResult<IEnumerable<BiatecAsset>>> GetAssets([FromQuery] string? ids = null, [FromQuery] string? search = null, [FromQuery] int offset = 0, [FromQuery] int size = 100)
        {
            try
            {
                size = Math.Clamp(size, 1, 500);
                IEnumerable<ulong>? parsedIds = null;
                if (!string.IsNullOrWhiteSpace(ids))
                {
                    parsedIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(s => ulong.TryParse(s, out var v) ? v : (ulong?)null)
                                    .Where(v => v.HasValue)
                                    .Select(v => v!.Value)
                                    .Distinct()
                                    .ToArray();
                }

                var assets = await _assetRepository.GetAssetsAsync(parsedIds, search, offset, size, HttpContext.RequestAborted);

                return Ok(assets);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { error = "Request canceled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list assets");
                return StatusCode(500, new { error = "Failed to list assets" });
            }
        }

        /// Returns image for asset by id
        [HttpGet("image/{assetId}")]
        public async Task<IActionResult> GetAssetImage(ulong assetId)
        {
            try
            {
                if (assetId == 0)
                {
                    SetImageCacheHeaders();
                    return File(Encoding.UTF8.GetBytes(NativeAlgoLogoSvg), "image/svg+xml");
                }

                var cancellationToken = HttpContext.RequestAborted;

                var processor = new MainnetImageProcessor();
                var data = await processor.LoadImageAsync(assetId, cancellationToken);
                if (data.Length > 100)
                {
                    SetImageCacheHeaders();
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

        private void SetImageCacheHeaders()
        {
            Response.Headers["Cache-Control"] = "public,max-age=604800";
            Response.Headers["Expires"] = DateTime.UtcNow.AddDays(7).ToString("R");
        }
    }
}
