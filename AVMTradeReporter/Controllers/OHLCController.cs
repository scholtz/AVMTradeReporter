using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    /// <summary>
    /// TradingView UDF-compatible OHLC/charting datafeed (config, symbol resolution, search, bars,
    /// marks, quotes). Publicly accessible (no authentication required) so the charting widget can be
    /// embedded and queried directly from the browser without an ARC-14 signed transaction.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class OHLCController : ControllerBase
    {
        private readonly IOHLCService _ohlcService;
        private readonly IAssetRepository _assetRepository;
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;

        public OHLCController(IOHLCService ohlcService, IAssetRepository assetRepository, AggregatedPoolRepository aggregatedPoolRepository)
        {
            _ohlcService = ohlcService;
            _assetRepository = assetRepository;
            _aggregatedPoolRepository = aggregatedPoolRepository;
        }

        /// <summary>Returns the TradingView UDF datafeed configuration (supported resolutions, exchanges, symbol types).</summary>
        [HttpGet("config")]
        public IActionResult GetConfig() => Ok(_ohlcService.GetConfig());

        /// <summary>Returns the current server time as a Unix timestamp, used by TradingView for datafeed sync.</summary>
        [HttpGet("time")]
        public IActionResult GetTime() => Ok(_ohlcService.GetTime());

        /// <summary>Resolves a single symbol (e.g. "31566704_0") to its TradingView symbol info.</summary>
        /// <param name="symbol">Underscore-separated asset id pair, e.g. "assetA_assetB".</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>200 with symbol info, or 404 if the symbol is not resolvable.</returns>
        [HttpGet("symbols")]
        public async Task<IActionResult> GetSymbol([FromQuery] string symbol, CancellationToken ct)
        {
            var res = await _ohlcService.GetSymbolAsync(symbol, ct);
            if (res == null) return NotFound();
            return Ok(res);
        }

        /// <summary>Resolves a comma separated group of symbols to their TradingView symbol info in bulk.</summary>
        /// <param name="group">Comma separated list of underscore-separated asset id pairs.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("symbol_info")]
        public async Task<IActionResult> GetSymbolInfo([FromQuery] string group, CancellationToken ct)
        {
            // TradingView passes symbols as comma separated in 'group' or 'symbol'
            var res = await _ohlcService.GetSymbolInfoAsync(group, ct);
            return Ok(res);
        }

        /// <summary>Searches for tradable symbols (asset pairs) matching a free-text query, for the TradingView symbol search box.</summary>
        /// <param name="query">Free-text search term (asset name/unit name).</param>
        /// <param name="type">Optional symbol type filter.</param>
        /// <param name="limit">Maximum number of results to return (default 30).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] string? type, [FromQuery] int limit = 30, CancellationToken ct = default)
        {
            var res = await _ohlcService.SearchAsync(query, limit, ct);
            return Ok(res);
        }

        /// <summary>Returns chart marks (annotations) for the TradingView datafeed. Currently always empty.</summary>
        [HttpGet("marks")]
        public IActionResult GetMarks() => Ok(_ohlcService.GetMarks());

        /// <summary>Returns timescale marks for the TradingView datafeed. Currently always empty.</summary>
        [HttpGet("timescale_marks")]
        public IActionResult GetTimescaleMarks() => Ok(_ohlcService.GetTimescaleMarks());

        /// <summary>Returns last-quote snapshots for the given symbols, for the TradingView quotes API.</summary>
        /// <param name="symbols">Comma separated list of underscore-separated asset id pairs.</param>
        [HttpGet("quotes")]
        public IActionResult GetQuotes([FromQuery] string symbols) => Ok(_ohlcService.GetQuotes(symbols));

        /// <summary>Returns OHLCV bars for an asset pair over a time range, for the TradingView history API.</summary>
        /// <param name="assetA">First asset id of the pair.</param>
        /// <param name="assetB">Second asset id of the pair.</param>
        /// <param name="resolution">TradingView resolution string (e.g. "1", "60", "1D").</param>
        /// <param name="from">Range start, Unix timestamp (seconds).</param>
        /// <param name="to">Range end, Unix timestamp (seconds).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] ulong assetA, [FromQuery] ulong assetB, [FromQuery] string resolution, [FromQuery] long from, [FromQuery] long to, CancellationToken ct)
        {
            var res = await _ohlcService.GetHistoryAsync(assetA, assetB, resolution, from, to, ct);
            return Ok(res);
        }
    }
}
