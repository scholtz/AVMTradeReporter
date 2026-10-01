using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
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
    /// and are dynamically mapped as text (see <c>TradeQueryService.AddDualKeywordTerm</c>).
    /// </summary>
    public sealed class ElasticCoinGeckoEventSource : ICoinGeckoEventSource
    {
        private readonly ElasticsearchClient? _elastic;
        private readonly IReadOnlyList<DEXProtocol> _protocols;

        public ElasticCoinGeckoEventSource(IServiceProvider services, IOptions<AppConfiguration> options)
        {
            _elastic = services.GetService<ElasticsearchClient>();
            _protocols = options.Value.CoinGecko.Protocols;
        }

        public bool IsAvailable => _elastic != null;

        public async Task<IReadOnlyList<Trade>> GetTradesAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
        {
            if (_elastic == null) throw new InvalidOperationException("Elasticsearch is not configured");
            var response = await _elastic.SearchAsync<Trade>(s => s
                .Indices("trades")
                .Size(size)
                .TrackTotalHits(new TrackHits(false))
                .Sort(so => so.Field(f => f.Field(t => t.BlockId).Order(SortOrder.Asc)))
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Range(r => r.Number(n => n.Field(t => t.BlockId).Gte(lo).Lte(hi))),
                    f => DualKeywordTerms(f, "tradeState", new[] { nameof(TxState.Confirmed) }),
                    f => DualKeywordTerms(f, "protocol", _protocols.Select(p => p.ToString()))))),
                cancellationToken);
            if (!response.IsValidResponse) throw new InvalidOperationException($"Elasticsearch trades query failed: {response.DebugInformation}");
            return response.Documents.ToList();
        }

        public async Task<IReadOnlyList<Liquidity>> GetLiquidityAsync(ulong lo, ulong hi, int size, CancellationToken cancellationToken)
        {
            if (_elastic == null) throw new InvalidOperationException("Elasticsearch is not configured");
            var response = await _elastic.SearchAsync<Liquidity>(s => s
                .Indices("liquidity")
                .Size(size)
                .TrackTotalHits(new TrackHits(false))
                .Sort(so => so.Field(f => f.Field(t => t.BlockId).Order(SortOrder.Asc)))
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Range(r => r.Number(n => n.Field(t => t.BlockId).Gte(lo).Lte(hi))),
                    f => DualKeywordTerms(f, "txState", new[] { nameof(TxState.Confirmed) }),
                    f => DualKeywordTerms(f, "protocol", _protocols.Select(p => p.ToString()))))),
                cancellationToken);
            if (!response.IsValidResponse) throw new InvalidOperationException($"Elasticsearch liquidity query failed: {response.DebugInformation}");
            return response.Documents.Where(d => d.BlockId >= lo && d.BlockId <= hi).ToList();
        }

        /// <summary>Exact match that works for keyword mapped fields and for text fields with a ".keyword" subfield.</summary>
        private static void DualKeywordTerms<T>(QueryDescriptor<T> query, string field, IEnumerable<string> values)
        {
            var terms = values.Select(FieldValue.String).ToArray();
            query.Bool(b => b
                .Should(
                    s => s.Terms(t => t.Field(field).Terms(new TermsQueryField(terms))),
                    s => s.Terms(t => t.Field(field + ".keyword").Terms(new TermsQueryField(terms))))
                .MinimumShouldMatch(1));
        }
    }
}
