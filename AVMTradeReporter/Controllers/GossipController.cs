using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// Exposes the live connection health of the mempool gossip listener. Publicly accessible (no
    /// authentication required) so relay connectivity can be verified without cluster/SSH access.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class GossipController : ControllerBase
    {
        private readonly GossipBackgroundService _gossipBackgroundService;

        public GossipController(GossipBackgroundService gossipBackgroundService)
        {
            _gossipBackgroundService = gossipBackgroundService;
        }

        /// <summary>
        /// Returns the set of gossip relays currently connected (or being connected to), along with how many
        /// mempool messages each has delivered and when it last delivered one.
        /// </summary>
        [HttpGet("status")]
        [ProducesResponseType(typeof(IEnumerable<GossipRelayStatus>), StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<GossipRelayStatus>> GetStatus()
        {
            return Ok(_gossipBackgroundService.GetRelayStatus());
        }
    }
}
