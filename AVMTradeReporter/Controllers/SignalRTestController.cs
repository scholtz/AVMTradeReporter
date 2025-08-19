using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Data.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;
using System.Security.Claims;

namespace AVMTradeReporter.Controllers
{
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