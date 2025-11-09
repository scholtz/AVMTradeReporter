using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Model.DTO.OHLC;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace AVMTradeReporter.Services
{
    public class OHLCService : IOHLCService
    {
        private readonly ElasticsearchClient _elastic;
        private readonly IAssetRepository _assetRepo;
        private readonly AggregatedPoolRepository _aggRepo;
        private static readonly Dictionary<string, string> _resolutionMap = new(StringComparer.Ordinal)
        {
            {"1","1m"}, {"1m","1m"},
            {"5","5m"}, {"5m","5m"},
            {"15","15m"}, {"15m","15m"},
            {"60","1h"}, {"1h","1h"},
            {"240","4h"}, {"4h","4h"},
            {"D","1d"}, {"1D","1d"}, {"1d","1d"},
            {"W","1w"}, {"1W","1w"}, {"1w","1w"},
            {"M","1M"}, {"1M","1M"}
        };
        private static readonly string[] _supportedResolutions = { "1", "5", "15", "60", "240", "1D", "1W", "1M" };

        public OHLCService(ElasticsearchClient elastic, IAssetRepository assetRepo, AggregatedPoolRepository aggRepo)
        {
            _elastic = elastic;
            _assetRepo = assetRepo;
            _aggRepo = aggRepo;
        }

        public object GetConfig() => new OHLCConfigDto
        {
            Supports_Search = true,
            Supports_Group_Request = false,
            Supports_Marks = false,
            Supports_Timescale_Marks = false,
            Supports_Time = true,
            Supported_Resolutions = _supportedResolutions
        };

        public long GetTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private (ulong a, ulong b)? ParseSymbol(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            var parts = symbol.Replace("_", "-").Split('-', '/', ':');
            if (parts.Length < 2) return null;
            if (ulong.TryParse(parts[0], out var a) && ulong.TryParse(parts[1], out var b))
                return (a, b);
            return null;
        }

        public async Task<object?> GetSymbolAsync(string symbol, CancellationToken ct)
        {
            var parsed = ParseSymbol(symbol);
            if (parsed == null) return null;
            var (a, b) = parsed.Value;
            var assetA = await _assetRepo.GetAssetAsync(a, ct);
            var assetB = await _assetRepo.GetAssetAsync(b, ct);
            if (assetA == null || assetB == null) return null;
            var decA = assetA.Params?.Decimals ?? 6;
            var decB = assetB.Params?.Decimals ?? 6;
            var priceScale = (int)Math.Pow(10, Math.Max(decA, decB));
            var name = $"{a}-{b}";
            return new SymbolDto
            {
                Name = name,
                Ticker = name,
                Description = $"{assetA.Params?.UnitName ?? a.ToString()} / {assetB.Params?.UnitName ?? b.ToString()}",
                Pricescale = priceScale,
                Supported_Resolutions = _supportedResolutions
            };
        }

        public async Task<IEnumerable<object>> SearchAsync(string query, int limit, CancellationToken ct)
        {
            query = query?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(query)) return Array.Empty<object>();
            var assets = await _assetRepo.GetAssetsAsync(null, query, 0, limit, ct);
            return assets.Select(a => (object)new SearchSymbolDto
            {
                Symbol = a.Index.ToString(),
                Full_Name = a.Index.ToString(),
                Description = a.Params?.Name ?? a.Params?.UnitName ?? a.Index.ToString(),
                Ticker = a.Index.ToString(),
            });
        }

        public object GetMarks() => Array.Empty<object>();
        public object GetTimescaleMarks() => Array.Empty<object>();

        public object GetQuotes(string symbols)
        {
            var list = symbols?.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            var resp = new QuotesResponseDto();
            foreach (var raw in list)
            {
                var sym = raw.Replace("\"", "").Trim();
                var parsed = ParseSymbol(sym);
                if (parsed == null) continue;
                var (a, b) = parsed.Value;
                var pool = _aggRepo.GetAggregatedPool(a, b);
                if (pool == null) continue;
                var oriented = pool;
                if (pool.AssetIdA != a || pool.AssetIdB != b)
                    oriented = (pool.AssetIdA == b && pool.AssetIdB == a) ? pool.Reverse() : pool;
                decimal price = oriented.VirtualSumALevel1 > 0 ? oriented.VirtualSumBLevel1 / oriented.VirtualSumALevel1 : 0m;
                resp.D.Add(new QuoteEntryDto
                {
                    S = "ok",
                    N = sym,
                    V = new QuoteValueDto
                    {
                        Short_Name = sym,
                        Description = sym,
                        Price = price,
                        Volume = oriented.TVL_A + oriented.TVL_B,
                        Bid = price,
                        Ask = price,
                        High_Price = price,
                        Low_Price = price
                    }
                });
            }
            return resp;
        }

        public async Task<object> GetHistoryAsync(ulong assetA, ulong assetB, string resolution, long from, long to, CancellationToken ct)
        {
            if (_elastic == null) return new HistoryResponseDto { S = "no_data" };
            if (assetA == assetB) return new HistoryResponseDto { S = "no_data" };
            if (!_resolutionMap.TryGetValue(resolution ?? string.Empty, out var interval)) return new HistoryResponseDto { S = "error", Error = "Unsupported resolution" };

            var fromDt = DateTimeOffset.FromUnixTimeSeconds(from).UtcDateTime;
            var toDt = DateTimeOffset.FromUnixTimeSeconds(to).UtcDateTime;
            if (toDt <= fromDt) return new HistoryResponseDto { S = "no_data" };

            var a = Math.Min(assetA, assetB);
            var b = Math.Max(assetA, assetB);
            var invert = (a != assetA);

            // Explicit request object to avoid deep descriptor graphs
            var request = new SearchRequest<OHLC>("ohlc")
            {
                Size = 5000,
                Sort = new List<SortOptions>
                {
                    new SortOptions { Field = new FieldSort { Field = Infer.Field<OHLC>(f => f.StartTime) } }
                },
                Query = new BoolQuery
                {
                    Filter = new List<Query>
                    {
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.AssetIdA), Value = a },
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.AssetIdB), Value = b },
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.Interval), Value = interval },
                        new DateRangeQuery { Field = Infer.Field<OHLC>(f => f.StartTime), Gte = fromDt, Lte = toDt }
                    }
                }
            };

            var search = await _elastic.SearchAsync<OHLC>(request, ct);

            if (!search.IsValidResponse || search.Hits.Count == 0)
                return new HistoryResponseDto { S = "no_data" };

            var resp = new HistoryResponseDto { S = "ok", T = new(), O = new(), H = new(), L = new(), C = new(), V = new() };

            foreach (var doc in search.Documents.OrderBy(d => d.StartTime))
            {
                if (doc.Open == null || doc.High == null || doc.Low == null || doc.Close == null) continue;
                var ts = doc.StartTime.ToUnixTimeSeconds();
                resp.T.Add(ts);
                if (!invert)
                {
                    resp.O.Add(doc.Open.Value); resp.H.Add(doc.High.Value); resp.L.Add(doc.Low.Value); resp.C.Add(doc.Close.Value);
                    resp.V.Add(doc.VolumeBase ?? 0);
                }
                else
                {
                    decimal SafeInv(decimal x) => x == 0 ? 0 : 1 / x;
                    resp.O.Add(SafeInv(doc.Open.Value));
                    resp.H.Add(SafeInv(doc.Low.Value));
                    resp.L.Add(SafeInv(doc.High.Value));
                    resp.C.Add(SafeInv(doc.Close.Value));
                    resp.V.Add(doc.VolumeQuote ?? 0);
                }
            }
            if (resp.T.Count == 0) return new HistoryResponseDto { S = "no_data" };
            return resp;
        }

        public async Task<SymbolInfoDto> GetSymbolInfoAsync(string symbols, CancellationToken ct)
        {
            var list = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var symList = new List<string>();
            var tickers = new List<string>();
            var desc = new List<string>();
            var types = new List<string>();
            var exchListed = new List<string>();
            var exchTraded = new List<string>();
            var sessions = new List<string>();
            var timezones = new List<string>();
            var minmov = new List<int>();
            var pricescales = new List<int>();
            var hasIntraday = new List<bool>();
            var supportedRes = new List<string[]>();

            foreach (var raw in list)
            {
                var parsed = ParseSymbol(raw);
                if (parsed == null) continue;
                var (a, b) = parsed.Value;
                var assetA = await _assetRepo.GetAssetAsync(a, ct);
                var assetB = await _assetRepo.GetAssetAsync(b, ct);
                if (assetA == null || assetB == null) continue;
                var decA = assetA.Params?.Decimals ?? 6;
                var decB = assetB.Params?.Decimals ?? 6;
                var priceScale = (int)Math.Pow(10, Math.Max(decA, decB));
                var name = $"{a}-{b}";
                symList.Add(name);
                tickers.Add(name);
                desc.Add($"{assetA.Params?.UnitName ?? a.ToString()} / {assetB.Params?.UnitName ?? b.ToString()}");
                types.Add("crypto");
                exchListed.Add("ALG");
                exchTraded.Add("ALG");
                sessions.Add("24x7");
                timezones.Add("Etc/UTC");
                minmov.Add(1);
                pricescales.Add(priceScale);
                hasIntraday.Add(true);
                supportedRes.Add(_supportedResolutions);
            }

            return new SymbolInfoDto
            {
                Symbols = symList.ToArray(),
                Tickers = tickers.ToArray(),
                Description = desc.ToArray(),
                Type = types.ToArray(),
                ExchangeListed = exchListed.ToArray(),
                ExchangeTraded = exchTraded.ToArray(),
                Session = sessions.ToArray(),
                Timezone = timezones.ToArray(),
                Minmov = minmov.ToArray(),
                Pricescale = pricescales.ToArray(),
                HasIntraday = hasIntraday.ToArray(),
                SupportedResolutions = supportedRes.ToArray()
            };
        }
    }
}
