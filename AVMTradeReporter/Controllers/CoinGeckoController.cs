using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// GeckoTerminal / CoinGecko non-EVM DEX integration (spec: "GeckoTerminal Integration API Standards v0.1").
    /// Publicly accessible (no authentication): the integration is polled by CoinGecko's indexer, which cannot sign
    /// ARC-14 tokens, and serves public market data of the Biatec DEX only. Pair id = pool application id,
    /// asset id = ASA id (ALGO = 0); all amounts are decimalized strings.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/coingecko")]
    public class CoinGeckoController : ControllerBase
    {
        private readonly ICoinGeckoService _service;
        private readonly CoinGeckoConfiguration _config;

        public CoinGeckoController(ICoinGeckoService service, IOptions<AppConfiguration> options)
        {
            _service = service;
            _config = options.Value.CoinGecko;
        }

        /// <summary>
        /// Latest block for which every event is available through <c>/events</c> (never ahead of the data).
        /// </summary>
        [HttpGet("latest-block")]
        [ProducesResponseType(typeof(CoinGeckoLatestBlockResponse), 200)]
        public async Task<IActionResult> GetLatestBlock(CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return NotFound();
            var result = await _service.GetLatestBlockAsync(cancellationToken);
            return ToActionResult(result, maxAgeSeconds: 1);
        }

        /// <summary>Asset (ASA) details with decimalized total supply.</summary>
        /// <param name="id">Asset id, ALGO is 0.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [HttpGet("asset")]
        [ProducesResponseType(typeof(CoinGeckoAssetResponse), 200)]
        public async Task<IActionResult> GetAsset([FromQuery] string? id, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return NotFound();
            var result = await _service.GetAssetAsync(id, cancellationToken);
            return ToActionResult(result, maxAgeSeconds: _config.AssetCacheSeconds);
        }

        /// <summary>Pair (pool) details. asset0 / asset1 follow the on-chain asset A / asset B order and never change.</summary>
        /// <param name="id">Pool application id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [HttpGet("pair")]
        [ProducesResponseType(typeof(CoinGeckoPairResponse), 200)]
        public async Task<IActionResult> GetPair([FromQuery] string? id, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return NotFound();
            var result = await _service.GetPairAsync(id, cancellationToken);
            return ToActionResult(result, maxAgeSeconds: 300);
        }

        /// <summary>
        /// Swap / join / exit events of the inclusive block range, sorted by transaction then event index.
        /// </summary>
        /// <param name="fromBlock">First block (inclusive).</param>
        /// <param name="toBlock">Last block (inclusive); must not exceed <c>latest-block</c> and the range is limited to <c>MaxBlockSpan</c> blocks.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [HttpGet("events")]
        [ProducesResponseType(typeof(CoinGeckoEventsResponse), 200)]
        public async Task<IActionResult> GetEvents([FromQuery] ulong? fromBlock, [FromQuery] ulong? toBlock, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return NotFound();
            if (fromBlock == null || toBlock == null) return BadRequest(new { error = "Query parameters 'fromBlock' and 'toBlock' are required" });

            var result = await _service.GetEventsJsonAsync(fromBlock.Value, toBlock.Value, cancellationToken);
            if (result.Outcome != CoinGeckoOutcome.Ok) return ToActionResult<byte[]>(result, 0);
            // Ranges served here are at or below latest-block, i.e. immutable.
            Response.Headers.CacheControl = $"public, max-age={Math.Max(1, _config.EventsCacheSeconds)}";
            return File(result.Value!, "application/json");
        }

        private IActionResult ToActionResult<T>(CoinGeckoResult<T> result, int maxAgeSeconds) where T : class
        {
            switch (result.Outcome)
            {
                case CoinGeckoOutcome.Ok:
                    Response.Headers.CacheControl = $"public, max-age={Math.Max(0, maxAgeSeconds)}";
                    return Ok(result.Value);
                case CoinGeckoOutcome.NotFound:
                    return NotFound(new { error = result.Error });
                case CoinGeckoOutcome.BadRequest:
                    return BadRequest(new { error = result.Error });
                default:
                    Response.Headers.RetryAfter = "2";
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Error });
            }
        }
    }
}
