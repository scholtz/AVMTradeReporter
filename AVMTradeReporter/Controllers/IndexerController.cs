using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/indexer")]
    public class IndexerController : ControllerBase
    {
        private readonly ILogger<IndexerController> _logger;

        public IndexerController(ILogger<IndexerController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the current indexer status
        /// </summary>
        /// <returns>The current indexer object with round information</returns>
        [HttpGet("status")]
        [ProducesResponseType(typeof(Indexer), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<Indexer> GetIndexerStatus()
        {
            var indexer = TradeReporterBackgroundService.Indexer;
            
            if (indexer == null)
            {
                _logger.LogWarning("Indexer is not available");
                return NotFound("Indexer is not available");
            }

            _logger.LogDebug("Retrieved indexer: {IndexerId}, Round: {Round}", indexer.Id, indexer.Round);
            return Ok(indexer);
        }

    }
}