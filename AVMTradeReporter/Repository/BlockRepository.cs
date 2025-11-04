using Algorand.Algod;
using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Diagnostics;

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
                var subscriptions = BiatecScanHub.GetSubscriptions();

                if (!subscriptions.Any())
                {
                    _logger.LogDebug("No active subscriptions, skipping trade publication");
                    return;
                }

                var subscribedClientsConnections = new HashSet<string>();

                foreach (var subscription in subscriptions)
                {
                    var userId = subscription.Key;
                    var filter = subscription.Value;

                    if (BiatecScanHub.ShouldBlockToUser(block, filter))
                    {
                        subscribedClientsConnections.Add(userId);
                    }
                }
                _logger.LogInformation("Published block #{round} to {N} subscribed users at SignalR hub, time diff {diff}", block.Round, subscribedClientsConnections.Count, DateTimeOffset.Now - block.Timestamp);

                //await _hubContext.Clients.Clients(subscribedClientsConnections).SendAsync(BiatecScanHub.Subscriptions.BLOCK, block, cancellationToken);

                await _hubContext.Clients.All.SendAsync(BiatecScanHub.Subscriptions.BLOCK, block, cancellationToken);

                BiatecScanHub.RecentBlockUpdates.Enqueue(block);
                if (BiatecScanHub.RecentBlockUpdates.Count > 10)
                {
                    BiatecScanHub.RecentBlockUpdates.TryDequeue(out _);
                }


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish block");
            }
        }
    }
}
