using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services.CoinGecko
{
    /// <summary>Where the integration reads confirmed swaps and liquidity changes of a block range from.</summary>
    public interface ICoinGeckoEventSource
    {
        bool IsAvailable { get; }

        /// <summary>
        /// Confirmed swaps of the published protocols with <c>lo &lt;= blockId &lt;= hi</c>, at most <paramref name="size"/>
        /// of them. A result of exactly <paramref name="size"/> documents means "maybe truncated".
        /// </summary>
        Task<IReadOnlyList<Trade>> GetTradesAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken);

        /// <summary>Same as <see cref="GetTradesAsync"/> for liquidity deposits / withdrawals.</summary>
        Task<IReadOnlyList<Liquidity>> GetLiquidityAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Elasticsearch implementation. Filters only on <c>blockId</c> (a numeric field in every mapping variant), the
    /// confirmed state and the protocol, and sorts only by <c>blockId</c>: sorting or searching-after on
    /// <c>txId</c> would fail on the production "trades" index, whose string fields predate the index template
    /// and are dynamically mapped as text (see <see cref="ElasticKeywordQuery"/>).
    /// </summary>
    public sealed class ElasticCoinGeckoEventSource : ICoinGeckoEventSource
    {
        private readonly ElasticsearchClient? _elastic;
        private readonly string[] _protocols;

        public ElasticCoinGeckoEventSource(IServiceProvider services, IOptions<AppConfiguration> options)
        {
            _elastic = services.GetService<ElasticsearchClient>();
            _protocols = options.Value.CoinGecko.PublishedProtocols.Select(p => p.ToString()).ToArray();
        }

        public bool IsAvailable => _elastic != null;

        public Task<IReadOnlyList<Trade>> GetTradesAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            => SearchRangeAsync<Trade>("trades", "tradeState", t => t.BlockId, lo, hi, size, cancellationToken);

        public Task<IReadOnlyList<Liquidity>> GetLiquidityAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
            => SearchRangeAsync<Liquidity>("liquidity", "txState", l => l.BlockId, lo, hi, size, cancellationToken);

        private async Task<IReadOnlyList<T>> SearchRangeAsync<T>(string index, string stateField, Func<T, ulong> blockOf, ulong lo, ulong hi, int size, CancellationToken cancellationToken)
        {
            if (_elastic == null) throw new InvalidOperationException("Elasticsearch is not configured");
            var response = await _elastic.SearchAsync<T>(s => s
                .Indices(index)
                .Size(size)
                .TrackTotalHits(new TrackHits(false))
                .Sort(so => so.Field(f => f.Field("blockId").Order(SortOrder.Asc)))
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Range(r => r.Number(n => n.Field("blockId").Gte(lo).Lte(hi))),
                    f => ElasticKeywordQuery.DualKeywordTerms(f, stateField, new[] { nameof(TxState.Confirmed) }),
                    f => ElasticKeywordQuery.DualKeywordTerms(f, "protocol", _protocols)))),
                cancellationToken);
            if (!response.IsValidResponse) throw new InvalidOperationException($"Elasticsearch {index} query failed: {response.DebugInformation}");
            // The bisection in CoinGeckoService relies on every document being inside [lo, hi]; keep that true whatever the
            // index mapping does with the range query.
            return response.Documents.Where(d => blockOf(d) >= lo && blockOf(d) <= hi).ToList();
        }
    }
}
