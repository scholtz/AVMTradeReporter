using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Authorize]
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

        [HttpPost("test-broadcast")]
        public async Task<IActionResult> TestBroadcast([FromBody] string message)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("TestMessage", message);
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
                    TradeState = TradeState.TxPool,
                    BlockId = 0,
                    TxGroup = "TEST_GROUP",
                    TopTxId = "TEST_TOP_TX"
                };

                await _hubContext.Clients.All.SendAsync("TradeUpdated", testTrade);
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