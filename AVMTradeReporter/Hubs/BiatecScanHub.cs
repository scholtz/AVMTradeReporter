using AVMTradeReporter.Model.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Hubs
{

    
    public class BiatecScanHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> User2Subscription = new ConcurrentDictionary<string, string>();
        [Authorize]
        public async Task Subscribe(string filter)
        {
            try
            {
                var userId = Context?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId)) throw new Exception("userId is empty");
                
                User2Subscription[userId] = filter;
                await Clients.Caller.SendAsync("Subscribed", filter);
            }
            catch (Exception e)
            {
                await Clients.Caller.SendAsync("Error", e.Message);
            }
        }
        [Authorize]
        public async Task Unsubscribe()
        {
            try
            {
                var userId = Context?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId)) throw new Exception("userId is empty");
                
                User2Subscription.TryRemove(userId, out _);
                await Clients.Caller.SendAsync("Unsubscribed");
            }
            catch (Exception e)
            {
                await Clients.Caller.SendAsync("Error", e.Message);
            }
        }
        [Authorize]
        public override async Task OnConnectedAsync()
        {
            // Log connection for debugging
            var userId = Context?.User?.Identity?.Name;
            Console.WriteLine($"SignalR client connected: {userId}");
            await base.OnConnectedAsync();
        }
        [Authorize]
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                User2Subscription.TryRemove(userId, out _);
                Console.WriteLine($"SignalR client disconnected: {userId}");
            }
            await base.OnDisconnectedAsync(exception);
        }

        // Static method to get current subscriptions (can be used by external services)
        public static IReadOnlyDictionary<string, string> GetSubscriptions()
        {
            return User2Subscription.ToArray().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
}
