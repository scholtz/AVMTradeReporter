using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
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
