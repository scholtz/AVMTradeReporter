using AVMTradeReporter.Model.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace AVMTradeReporter.Hubs
{
    public class BiatecScanHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> User2Subscription = new ConcurrentDictionary<string, string>();
        public static readonly ConcurrentQueue<Trade> RecentTrades = new ConcurrentQueue<Trade>();
        public static readonly ConcurrentQueue<Liquidity> RecentLiquidityUpdates = new ConcurrentQueue<Liquidity>();
        public static readonly ConcurrentQueue<Pool> RecentPoolUpdates = new ConcurrentQueue<Pool>();
        public static readonly ConcurrentQueue<AggregatedPool> RecentAggregatedPoolUpdates = new ConcurrentQueue<AggregatedPool>();
        public static AggregatedPool? ALGOUSD = null;

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
                await Clients.Caller.SendAsync("TestConnectionResult", connectionInfo);
            }
            catch (Exception e)
            {
                Console.WriteLine($"TestConnection error: {e.Message}");
                await Clients.Caller.SendAsync("Error", e.Message);
            }
        }

        [Authorize]
        public async Task Subscribe(string filter)
        {
            try
            {
                // Enhanced user identification with debugging
                var userId = GetUserId();
                Console.WriteLine($"Subscribe attempt - UserId: '{userId}', Filter: '{filter}'");
                Console.WriteLine($"User.Identity.Name: '{Context?.User?.Identity?.Name}'");
                Console.WriteLine($"UserIdentifier: '{Context?.UserIdentifier}'");
                Console.WriteLine($"ConnectionId: '{Context?.ConnectionId}'");
                Console.WriteLine($"User.Identity.IsAuthenticated: {Context?.User?.Identity?.IsAuthenticated}");
                Console.WriteLine($"User.Identity.AuthenticationType: '{Context?.User?.Identity?.AuthenticationType}'");

                // Print all claims for debugging
                if (Context?.User?.Claims != null)
                {
                    Console.WriteLine("User Claims:");
                    foreach (var claim in Context.User.Claims)
                    {
                        Console.WriteLine($"  {claim.Type}: {claim.Value}");
                    }
                }

                if (string.IsNullOrEmpty(userId))
                {
                    await Clients.Caller.SendAsync("Error", "User identification failed");
                    return;
                }

                User2Subscription[userId] = filter;

                await Clients.Caller.SendAsync("Subscribed", filter);
                await Clients.User(userId).SendAsync("AggregatedPoolUpdated", ALGOUSD);

                foreach (var trade in RecentTrades.OrderBy(t => t.Timestamp))
                {
                    // Also send filtered trades to specific users based on their subscriptions
                    if (BiatecScanHub.ShouldSendTradeToUser(trade, filter))
                    {
                        await Clients.User(userId).SendAsync("FilteredTradeUpdated", trade);
                    }
                }
                foreach (var item in RecentLiquidityUpdates.OrderBy(t => t.Timestamp))
                {
                    // Also send filtered trades to specific users based on their subscriptions
                    if (BiatecScanHub.ShouldSendLiquidityToUser(item, filter))
                    {
                        await Clients.User(userId).SendAsync("FilteredLiquidityUpdated", item);
                    }
                }
                foreach (var item in RecentPoolUpdates.OrderBy(t => t.Timestamp))
                {
                    // Also send filtered trades to specific users based on their subscriptions
                    if (BiatecScanHub.ShouldSendPoolToUser(item, filter))
                    {
                        await Clients.User(userId).SendAsync("PoolUpdated", item);
                    }
                }
                foreach (var item in RecentAggregatedPoolUpdates.OrderBy(t => t.LastUpdated))
                {
                    // Also send filtered trades to specific users based on their subscriptions
                    if (BiatecScanHub.ShouldSendAggregatedPoolToUser(item, filter))
                    {
                        await Clients.User(userId).SendAsync("AggregatedPoolUpdated", item);
                    }
                }

                Console.WriteLine($"Successfully subscribed user '{userId}' with filter '{filter}'");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Subscribe error: {e.Message}");
                await Clients.Caller.SendAsync("Error", e.Message);
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
                    await Clients.Caller.SendAsync("Error", "User identification failed");
                    return;
                }

                User2Subscription.TryRemove(userId, out _);
                await Clients.Caller.SendAsync("Unsubscribed");
                Console.WriteLine($"Successfully unsubscribed user '{userId}'");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unsubscribe error: {e.Message}");
                await Clients.Caller.SendAsync("Error", e.Message);
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
        public static IReadOnlyDictionary<string, string> GetSubscriptions()
        {
            return User2Subscription.ToArray().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        public static bool ShouldSendTradeToUser(Trade trade, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true; // No filter means send all trades
            }

            try
            {
                // Simple filtering logic - can be enhanced based on requirements
                // Filter format examples:
                // "protocol:Biatec" - filter by protocol
                // "asset:123" - filter by asset ID (either in or out)
                // "trader:ADDR123" - filter by trader address
                // "pool:456" - filter by pool app ID

                var filterParts = filter.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (filterParts.Length != 2)
                {
                    return true; // Invalid filter format, send all
                }

                var filterType = filterParts[0].ToLowerInvariant();
                var filterValue = filterParts[1];

                return filterType switch
                {
                    "protocol" => trade.Protocol.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "asset" => trade.AssetIdIn.ToString() == filterValue || trade.AssetIdOut.ToString() == filterValue,
                    "trader" => trade.Trader.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "pool" => trade.PoolAppId.ToString() == filterValue,
                    "pooladdress" => trade.PoolAddress.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "state" => trade.TradeState.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    _ => true // Unknown filter type, send all
                };
            }
            catch
            {
                return true; // On error, send the trade
            }
        }
        public static bool ShouldSendLiquidityToUser(Liquidity item, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true; // No filter means send all trades
            }

            try
            {
                // Simple filtering logic - can be enhanced based on requirements
                // Filter format examples:
                // "protocol:Biatec" - filter by protocol
                // "asset:123" - filter by asset ID (either in or out)
                // "trader:ADDR123" - filter by trader address
                // "pool:456" - filter by pool app ID

                var filterParts = filter.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (filterParts.Length != 2)
                {
                    return true; // Invalid filter format, send all
                }

                var filterType = filterParts[0].ToLowerInvariant();
                var filterValue = filterParts[1];

                return filterType switch
                {
                    "protocol" => item.Protocol.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "asset" => item.AssetIdA.ToString() == filterValue || item.AssetIdB.ToString() == filterValue || item.AssetIdLP.ToString() == filterValue,
                    "trader" => item.LiquidityProvider.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "pool" => item.PoolAppId.ToString() == filterValue,
                    "pooladdress" => item.PoolAddress.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "state" => item.TxState.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    _ => true // Unknown filter type, send all
                };
            }
            catch
            {
                return true; // On error, send the trade
            }
        }
        public static bool ShouldSendPoolToUser(Pool item, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true; // No filter means send all trades
            }

            try
            {
                // Simple filtering logic - can be enhanced based on requirements
                // Filter format examples:
                // "protocol:Biatec" - filter by protocol
                // "asset:123" - filter by asset ID (either in or out)
                // "trader:ADDR123" - filter by trader address
                // "pool:456" - filter by pool app ID

                var filterParts = filter.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (filterParts.Length != 2)
                {
                    return true; // Invalid filter format, send all
                }

                var filterType = filterParts[0].ToLowerInvariant();
                var filterValue = filterParts[1];

                return filterType switch
                {
                    "protocol" => item.Protocol.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "asset" => item.AssetIdA.ToString() == filterValue || item.AssetIdB.ToString() == filterValue || item.AssetIdLP.ToString() == filterValue,
                    "pool" => item.PoolAppId.ToString() == filterValue,
                    "pooladdress" => item.PoolAddress.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    _ => true // Unknown filter type, send all
                };
            }
            catch
            {
                return true; // On error, send the trade
            }
        }
        public static bool ShouldSendAggregatedPoolToUser(AggregatedPool item, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true; // No filter means send all trades
            }

            try
            {
                // Simple filtering logic - can be enhanced based on requirements
                // Filter format examples:
                // "protocol:Biatec" - filter by protocol
                // "asset:123" - filter by asset ID (either in or out)
                // "trader:ADDR123" - filter by trader address
                // "pool:456" - filter by pool app ID

                var filterParts = filter.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (filterParts.Length != 2)
                {
                    return true; // Invalid filter format, send all
                }

                var filterType = filterParts[0].ToLowerInvariant();
                var filterValue = filterParts[1];

                return filterType switch
                {
                    "asset" => item.AssetIdA.ToString() == filterValue || item.AssetIdB.ToString() == filterValue,
                    _ => true // Unknown filter type, send all
                };
            }
            catch
            {
                return true; // On error, send the trade
            }
        }
    }
}
