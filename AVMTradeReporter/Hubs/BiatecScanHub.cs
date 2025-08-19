using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Subscription;
using Elastic.Clients.Elasticsearch.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;

namespace AVMTradeReporter.Hubs
{
    public class BiatecScanHub : Hub
    {
        private static readonly ConcurrentDictionary<string, SubscriptionFilter> User2Subscription = new ConcurrentDictionary<string, SubscriptionFilter>();
        public static readonly ConcurrentQueue<Trade> RecentTrades = new ConcurrentQueue<Trade>();
        public static readonly ConcurrentQueue<Liquidity> RecentLiquidityUpdates = new ConcurrentQueue<Liquidity>();
        public static readonly ConcurrentQueue<Pool> RecentPoolUpdates = new ConcurrentQueue<Pool>();
        public static readonly ConcurrentQueue<AggregatedPool> RecentAggregatedPoolUpdates = new ConcurrentQueue<AggregatedPool>();
        public static readonly ConcurrentQueue<Model.Data.Block> RecentBlockUpdates = new ConcurrentQueue<Model.Data.Block>();
        public static AggregatedPool? ALGOUSD = null;

        public class Subscriptions
        {
            public const string TRADE = "Trade";
            public const string LIQUIDITY = "Liquidity";
            public const string BLOCK = "Block";
            public const string POOL = "Pool";
            public const string AGGREGATED_POOL = "AggregatedPool";
            public const string ERROR = "Error";
            public const string INFO = "Info";
        }


        // Test method without authorization for debugging
        public async Task TestConnection()
        {
            try
            {
                var userId = GetUserId();
                var isAuthenticated = Context?.User?.Identity?.IsAuthenticated ?? false;
                var connectionInfo = new
                {
                    UserId = userId,
                    ConnectionId = Context?.ConnectionId,
                    IsAuthenticated = isAuthenticated,
                    IdentityName = Context?.User?.Identity?.Name,
                    UserIdentifier = Context?.UserIdentifier,
                    AuthenticationType = Context?.User?.Identity?.AuthenticationType,
                    Claims = Context?.User?.Claims?.Select(c => new { c.Type, c.Value }).ToArray()
                };

                Console.WriteLine($"TestConnection - {System.Text.Json.JsonSerializer.Serialize(connectionInfo)}");
                await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.INFO, connectionInfo);
            }
            catch (Exception e)
            {
                Console.WriteLine($"TestConnection error: {e.Message}");
                await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.ERROR, e.Message);
            }
        }

        [Authorize]
        public async Task Subscribe(SubscriptionFilter filter)
        {
            try
            {
                // Enhanced user identification with debugging
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.ERROR, "User identification failed");
                    return;
                }

                User2Subscription[userId] = filter;

                await SendBasicData(userId, filter);
                await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.INFO, $"Subscribed to {JsonConvert.SerializeObject(filter)}");

                Console.WriteLine($"Successfully subscribed user '{userId}' with filter '{filter}'");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Subscribe error: {e.Message}");
                await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.ERROR, e.Message);
            }
        }

        public async Task SendBasicData(string userId, SubscriptionFilter filter)
        {
            if (filter.MainAggregatedPools)
            {
                await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.AGGREGATED_POOL, ALGOUSD);
            }
            if (filter.RecentBlocks)
            {
                foreach (var trade in RecentBlockUpdates.OrderBy(t => t.Timestamp))
                {
                    // Also send filtered trades to specific users based on their subscriptions
                    await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.BLOCK, trade);
                }
            }
            if (filter.RecentTrades)
            {
                foreach (var trade in RecentTrades.OrderBy(t => t.Timestamp))
                {
                    await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.TRADE, trade);
                }
            }
            if (filter.RecentLiquidity)
            {
                foreach (var item in RecentLiquidityUpdates.OrderBy(t => t.Timestamp))
                {
                    await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.LIQUIDITY, item);
                }
            }
            if (filter.RecentPool)
            {
                foreach (var item in RecentPoolUpdates.OrderBy(t => t.Timestamp))
                {
                    await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.POOL, item);
                }
            }
            if (filter.RecentAggregatedPool)
            {
                foreach (var item in RecentAggregatedPoolUpdates.OrderBy(t => t.LastUpdated))
                {
                    await Clients.User(userId).SendAsync(BiatecScanHub.Subscriptions.AGGREGATED_POOL, item);
                }
            }
        }

        [Authorize]
        public async Task Unsubscribe()
        {
            try
            {
                var userId = GetUserId();
                Console.WriteLine($"Unsubscribe attempt - UserId: '{userId}'");

                if (string.IsNullOrEmpty(userId))
                {
                    await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.ERROR, "User identification failed");
                    return;
                }

                User2Subscription.TryRemove(userId, out _);
                await Clients.Caller.SendAsync("Unsubscribed");
                Console.WriteLine($"Successfully unsubscribed user '{userId}'");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unsubscribe error: {e.Message}");
                await Clients.Caller.SendAsync(BiatecScanHub.Subscriptions.ERROR, e.Message);
            }
        }

        public override async Task OnConnectedAsync()
        {
            // Log connection for debugging - no [Authorize] needed here
            var userId = GetUserId();
            var isAuthenticated = Context?.User?.Identity?.IsAuthenticated ?? false;
            Console.WriteLine($"SignalR client connected - UserId: '{userId}', ConnectionId: '{Context?.ConnectionId}', Authenticated: {isAuthenticated}");

            if (isAuthenticated && Context?.User?.Claims != null)
            {
                Console.WriteLine("Connection Claims:");
                foreach (var claim in Context.User.Claims)
                {
                    Console.WriteLine($"  {claim.Type}: {claim.Value}");
                }
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = GetUserId();
            if (!string.IsNullOrEmpty(userId))
            {
                User2Subscription.TryRemove(userId, out _);
                Console.WriteLine($"SignalR client disconnected - UserId: '{userId}', ConnectionId: '{Context.ConnectionId}'");
            }
            await base.OnDisconnectedAsync(exception);
        }

        private string GetUserId()
        {
            // Try multiple ways to get user identification

            // First try: User.Identity.Name
            if (!string.IsNullOrEmpty(Context?.User?.Identity?.Name))
            {
                return Context.User.Identity.Name;
            }

            // Second try: UserIdentifier (set by authentication handler)
            if (!string.IsNullOrEmpty(Context?.UserIdentifier))
            {
                return Context.UserIdentifier;
            }

            // Third try: Look for specific claim types that Algorand auth might use
            if (Context?.User?.Claims != null)
            {
                // Check for common claim types
                var subjectClaim = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(subjectClaim))
                {
                    return subjectClaim;
                }

                var nameClaim = Context.User.FindFirst(ClaimTypes.Name)?.Value;
                if (!string.IsNullOrEmpty(nameClaim))
                {
                    return nameClaim;
                }

                // Check for "sub" claim (common in JWT)
                var subClaim = Context.User.FindFirst("sub")?.Value;
                if (!string.IsNullOrEmpty(subClaim))
                {
                    return subClaim;
                }

                // Check for any claim that might contain the user ID
                var firstClaim = Context.User.Claims.FirstOrDefault()?.Value;
                if (!string.IsNullOrEmpty(firstClaim))
                {
                    return firstClaim;
                }
            }

            // Last resort: Use ConnectionId if authenticated
            if (Context?.User?.Identity?.IsAuthenticated == true)
            {
                return Context.ConnectionId;
            }

            return string.Empty;
        }

        // Static method to get current subscriptions (can be used by external services)
        public static IReadOnlyDictionary<string, SubscriptionFilter> GetSubscriptions()
        {
            return User2Subscription.ToArray().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        public static bool ShouldSendTradeToUser(Trade trade, SubscriptionFilter filter)
        {
            if (filter.RecentTrades) return true;
            if (filter.PoolsAddresses.Contains(trade.PoolAddress)) return true;
            var aggregatedPoolId = $"{trade.AssetIdIn}-{trade.AssetIdOut}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolId)) return true;
            var aggregatedPoolIdReverted = $"{trade.AssetIdOut}-{trade.AssetIdIn}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolIdReverted)) return true;
            return false;
        }
        public static bool ShouldSendLiquidityToUser(Liquidity item, SubscriptionFilter filter)
        {
            if (filter.RecentLiquidity) return true;
            if (filter.PoolsAddresses.Contains(item.PoolAddress)) return true;
            var aggregatedPoolId = $"{item.AssetIdA}-{item.AssetIdB}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolId)) return true;
            var aggregatedPoolIdReverted = $"{item.AssetIdB}-{item.AssetIdA}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolIdReverted)) return true;
            return false;
        }
        public static bool ShouldBlockToUser(AVMTradeReporter.Model.Data.Block item, SubscriptionFilter filter)
        {
            if (filter.RecentBlocks) return true;
            return false;
        }
        public static bool ShouldSendPoolToUser(Pool item, SubscriptionFilter filter)
        {
            if (filter.RecentTrades) return true;
            if (filter.PoolsAddresses.Contains(item.PoolAddress)) return true;
            var aggregatedPoolId = $"{item.AssetIdA}-{item.AssetIdB}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolId)) return true;
            var aggregatedPoolIdReverted = $"{item.AssetIdB}-{item.AssetIdA}";
            if (filter.AggregatedPoolsIds.Contains(aggregatedPoolIdReverted)) return true;

            return false;
        }
        public static bool ShouldSendAggregatedPoolToUser(AggregatedPool item, SubscriptionFilter filter)
        {
            if (filter.AggregatedPoolsIds.Contains(item.Id)) return true;
            return false;
        }
    }
}
