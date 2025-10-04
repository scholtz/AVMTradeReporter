using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
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

        [HttpGet("config")]
        public IActionResult GetConfig() => Ok(_ohlcService.GetConfig());

        [HttpGet("time")]
        public IActionResult GetTime() => Ok(_ohlcService.GetTime());

        [HttpGet("symbols")]
        public async Task<IActionResult> GetSymbol([FromQuery] string symbol, CancellationToken ct)
        {
            var res = await _ohlcService.GetSymbolAsync(symbol, ct);
            if (res == null) return NotFound();
            return Ok(res);
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] string? type, [FromQuery] int limit = 30, CancellationToken ct = default)
        {
            var res = await _ohlcService.SearchAsync(query, limit, ct);
            return Ok(res);
        }

        [HttpGet("marks")]
        public IActionResult GetMarks() => Ok(_ohlcService.GetMarks());

        [HttpGet("timescale_marks")]
        public IActionResult GetTimescaleMarks() => Ok(_ohlcService.GetTimescaleMarks());

        [HttpGet("quotes")]
        public IActionResult GetQuotes([FromQuery] string symbols) => Ok(_ohlcService.GetQuotes(symbols));

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] ulong assetA, [FromQuery] ulong assetB, [FromQuery] string resolution, [FromQuery] long from, [FromQuery] long to, CancellationToken ct)
        {
            var res = await _ohlcService.GetHistoryAsync(assetA, assetB, resolution, from, to, ct);
            return Ok(res);
        }
    }
}
