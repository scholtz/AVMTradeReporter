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
        [HttpGet("{assetId}")]
        public async Task<IActionResult> GetAssetImage(ulong assetId)
        {
            try
            {
                var cancellationToken = HttpContext.RequestAborted;

                var imagesDir = Path.Combine(AppContext.BaseDirectory, "images");
                Directory.CreateDirectory(imagesDir);

                var imagePath = Path.Combine(imagesDir, $"{assetId}.png");

                if (System.IO.File.Exists(imagePath))
                {
                    var imageBytes = await System.IO.File.ReadAllBytesAsync(imagePath, cancellationToken);
                    return File(imageBytes, "image/png");
                }

                var remoteUrl = $"https://asa-list.tinyman.org/assets/{assetId}/icon.png";
                using var httpClient = new HttpClient();

                using var response = await httpClient.GetAsync(remoteUrl, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // if not found, return transparent 1x1 pixel PNG
                    return File(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), "image/png");
                }

                response.EnsureSuccessStatusCode();

                var imageBytesFromRemote = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                await System.IO.File.WriteAllBytesAsync(imagePath, imageBytesFromRemote, cancellationToken);

                return File(imageBytesFromRemote, "image/png");
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
