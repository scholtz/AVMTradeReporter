using Algorand.Algod;
using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace AVMTradeReporter.Repository
{
    public class BlockRepository
    {
        private readonly ILogger<AssetRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;
        /// <summary>
        /// Initializes a new instance of the <see cref="BlockRepository"/> class.
        /// </summary>
        /// <param name="logger">The logger used to log diagnostic and operational messages.</param>
        /// <param name="hubContext">The SignalR hub context used for communication with connected clients.</param>
        public BlockRepository(
            ILogger<AssetRepository> logger,
            IHubContext<BiatecScanHub> hubContext)
        {
            _logger = logger;
            _hubContext = hubContext;
        }

        public async Task PublishToHub(Block block, CancellationToken cancellationToken)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("Block", block, cancellationToken);
                _logger.LogInformation("Published block #{round} to SignalR hub", block.Round);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish block");
            }
        }
    }
}
