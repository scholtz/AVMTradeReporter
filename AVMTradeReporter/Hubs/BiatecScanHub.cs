using AVMTradeReporter.Model.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Hubs
{
    [Authorize]
    public class BiatecScanHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> User2Subscription = new ConcurrentDictionary<string, string>();
        
        public async Task Subscribe(string filter)
        {
            try
            {
                var userId = Context.UserIdentifier;
                if (string.IsNullOrEmpty(userId)) throw new Exception("userId is empty");
                
                User2Subscription[userId] = filter;
                await Clients.Caller.SendAsync("Subscribed", filter);
            }
            catch (Exception e)
            {
                await Clients.Caller.SendAsync("Error", e.Message);
            }
        }

        public async Task Unsubscribe()
        {
            try
            {
                var userId = Context.UserIdentifier;
                if (string.IsNullOrEmpty(userId)) throw new Exception("userId is empty");
                
                User2Subscription.TryRemove(userId, out _);
                await Clients.Caller.SendAsync("Unsubscribed");
            }
            catch (Exception e)
            {
                await Clients.Caller.SendAsync("Error", e.Message);
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                User2Subscription.TryRemove(userId, out _);
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
