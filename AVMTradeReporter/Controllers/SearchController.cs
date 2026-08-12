using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// Free-text search across assets and pools. Requires ARC-14 authentication.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/search")] // GET api/search?q=algo
    public class SearchController : ControllerBase
    {
        private readonly ISearchService _searchService;
        private readonly ILogger<SearchController> _logger;

        public SearchController(ISearchService searchService, ILogger<SearchController> logger)
        {
            _searchService = searchService;
            _logger = logger;
        }

        /// <summary>
        /// Searches assets and pools matching the given query term.
        /// </summary>
        /// <param name="q">Free-text search term (asset name, unit name, or pool address).</param>
        /// <returns>200 with matching assets/pools.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(SearchResponse), 200)]
        public async Task<ActionResult<SearchResponse>> Search([FromQuery] string q)
        {
            try
            {
                var ct = HttpContext.RequestAborted;
                var res = await _searchService.SearchAsync(q, ct);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search failed");
                return StatusCode(500, "Search failed");
            }
        }
    }
}
