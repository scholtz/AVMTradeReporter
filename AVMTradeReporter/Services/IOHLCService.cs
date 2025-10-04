using Elastic.Clients.Elasticsearch;
using AVMTradeReporter.Model.DTO.OHLC;
namespace AVMTradeReporter.Services
{
    public interface IOHLCService
    {
        object GetConfig();
        long GetTime();
        Task<object?> GetSymbolAsync(string symbol, CancellationToken ct);
        Task<IEnumerable<object>> SearchAsync(string query, int limit, CancellationToken ct);
        object GetMarks();
        object GetTimescaleMarks();
        object GetQuotes(string symbols);
        Task<object> GetHistoryAsync(ulong assetA, ulong assetB, string resolution, long from, long to, CancellationToken ct);
        Task<SymbolInfoDto> GetSymbolInfoAsync(string symbols, CancellationToken ct);
    }
}
