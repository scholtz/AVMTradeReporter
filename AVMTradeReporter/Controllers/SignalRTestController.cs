using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;
using System.Security.Claims;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// Diagnostic/test utilities for verifying ARC-14 authentication and the SignalR hub broadcast
    /// pipeline. Requires ARC-14 authentication. Intended for development/debugging, not production
    /// client use.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/signalr")]
    public class SignalRTestController : ControllerBase
    {
        private readonly IHubContext<BiatecScanHub> _hubContext;
        private readonly ILogger<SignalRTestController> _logger;

        public SignalRTestController(IHubContext<BiatecScanHub> hubContext, ILogger<SignalRTestController> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>Returns the caller's current authentication state and claims, for debugging ARC-14 auth.</summary>
        [HttpGet("auth-test")]
        public IActionResult AuthTest()
        {
            var authInfo = new
            {
                IsAuthenticated = User?.Identity?.IsAuthenticated ?? false,
                Name = User?.Identity?.Name,
                AuthenticationType = User?.Identity?.AuthenticationType,
                Claims = User?.Claims?.Select(c => new { c.Type, c.Value }).ToArray(),
                Headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            };

            Console.WriteLine($"Auth test result: {System.Text.Json.JsonSerializer.Serialize(authInfo)}");
            return Ok(authInfo);
        }

        /// <summary>Same as <see cref="AuthTest"/> but forces authorization at the action level (redundant given the class-level [Authorize], kept explicit for clarity/testing).</summary>
        [HttpGet("auth-test-authorized")]
        [Authorize]
        public IActionResult AuthTestAuthorized()
        {
            var authInfo = new
            {
                IsAuthenticated = User?.Identity?.IsAuthenticated ?? false,
                Name = User?.Identity?.Name,
                AuthenticationType = User?.Identity?.AuthenticationType,
                Claims = User?.Claims?.Select(c => new { c.Type, c.Value }).ToArray(),
                Headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            };

            Console.WriteLine($"Authorized auth test result: {System.Text.Json.JsonSerializer.Serialize(authInfo)}");
            return Ok(authInfo);
        }

        /// <summary>Broadcasts a test info message to all connected SignalR clients.</summary>
        /// <param name="message">Free-text message to broadcast.</param>
        [HttpPost("test-broadcast")]
        [Authorize]
        public async Task<IActionResult> TestBroadcast([FromBody] string message)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync(BiatecScanHub.Subscriptions.INFO, message);
                _logger.LogInformation("Test message sent to all clients: {message}", message);
                return Ok(new { success = true, message = "Test message sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test message");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>Broadcasts a synthetic test trade to all connected SignalR clients, for verifying the trade event pipeline end to end.</summary>
        [HttpPost("test-trade")]
        [Authorize]
        public async Task<IActionResult> TestTrade()
        {
            try
            {
                var testTrade = new Trade
                {
                    TxId = "TEST_" + Guid.NewGuid().ToString("N")[..8],
                    AssetIdIn = 1,
                    AssetIdOut = 31566704,
                    AssetAmountIn = 1000000,
                    AssetAmountOut = 500000,
                    Protocol = DEXProtocol.Biatec,
                    Trader = "TEST_TRADER",
                    PoolAddress = "TEST_POOL",
                    PoolAppId = 123456,
                    Timestamp = DateTimeOffset.UtcNow,
                    TradeState = TxState.TxPool,
                    BlockId = 0,
                    TxGroup = "TEST_GROUP",
                    TopTxId = "TEST_TOP_TX"
                };

                await _hubContext.Clients.All.SendAsync(BiatecScanHub.Subscriptions.TRADE, testTrade);
                _logger.LogInformation("Test trade sent to all clients: {txId}", testTrade.TxId);
                return Ok(new { success = true, trade = testTrade });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test trade");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>Returns the current count and list of active SignalR hub subscriptions.</summary>
        [HttpGet("connections")]
        public IActionResult GetConnectionInfo()
        {
            var subscriptions = BiatecScanHub.GetSubscriptions();
            return Ok(new
            {
                connectionCount = subscriptions.Count,
                subscriptions = subscriptions
            });
        }
    }
}