using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using static Elastic.Clients.Elasticsearch.Field;

namespace AVMTradeReporter.Services
{
    public class TradeQueryService : ITradeQueryService
    {
        private readonly ElasticsearchClient? _elastic;
        private readonly ILogger<TradeQueryService> _logger;

        public TradeQueryService(IServiceProvider serviceProvider, ILogger<TradeQueryService> logger)
        {
            _elastic = serviceProvider.GetService<ElasticsearchClient>();
            _logger = logger;
        }

        public async Task<IEnumerable<Trade>> GetTradesAsync(
            ulong? assetIdIn = null,
            ulong? assetIdOut = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default)
        {
            var result = await GetTradesAsync(new TradeFilter
            {
                AssetIdIn = assetIdIn,
                AssetIdOut = assetIdOut,
                TxId = txId,
                Offset = offset,
                Size = size
            }, cancellationToken);

            return result.Items;
        }

        public async Task<PagedResult<Trade>> GetTradesAsync(TradeFilter filter, CancellationToken cancellationToken = default)
        {
            var trades = new List<Trade>();
            var normalizedFilter = NormalizeFilter(filter);

            try
            {
                if (_elastic == null)
                {
                    _logger.LogWarning("Elasticsearch client not available");
                    return CreatePagedResult(trades, 0, normalizedFilter);
                }

                var searchResponse = await _elastic.SearchAsync<Trade>(s => s
                    .Indices("trades")
                    .From(normalizedFilter.Offset)
                    .Size(normalizedFilter.Size)
                    .Sort(ss => BuildSort(ss, normalizedFilter))
                    .Query(q => BuildQuery(q, normalizedFilter)),
                    cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    trades.AddRange(searchResponse.Documents);
                    var total = searchResponse.Total;
                    return CreatePagedResult(trades, total, normalizedFilter);
                }
                else
                {
                    _logger.LogError("Elasticsearch query failed: {Error}", searchResponse.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch trades from Elasticsearch");
            }

            return CreatePagedResult(trades, trades.Count, normalizedFilter);
        }

        public async Task<Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>> GetPoolVolumesAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken = default)
        {
            var volumes = new Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>();

            if (_elastic == null)
            {
                _logger.LogWarning("Elasticsearch client not available for volume calculation");
                return volumes;
            }

            var poolAddressSet = new HashSet<string>(poolAddresses);

            var now = DateTimeOffset.UtcNow;

            // Periods
            var periods = new[]
            {
                (Hours: 1, Key: "1H"),
                (Hours: 24, Key: "24H"),
                (Hours: 168, Key: "7D") // 7*24
            };

            foreach (var period in periods)
            {
                var startTime = now.AddHours(-period.Hours);

                _logger.LogDebug("now: {now}, startTime for {period}: {startTime}", now, period.Key, startTime);

                try
                {
                    // Fetch trades in the period
                    var searchResponse = await _elastic.SearchAsync<Trade>(s => s
                        .Indices("trades")
                        .Size(200000) // Increased size to handle more trades per period
                        .Query(q => q
                            .Bool(b => b
                                .Must(
                                    m => m.Range(r => r.Date(dr => dr.Field(f => f.Timestamp).Gte(startTime.ToString("o")))),
                                    m => m.Terms(t => t.Field("poolAddress.keyword").Terms(poolAddressSet.Select(p => FieldValue.String(p)).ToArray())),
                                    m => m.Term(t => t.Field("tradeState.keyword").Value(FieldValue.String("Confirmed")))
                                )
                            )
                        ),
                        cancellationToken);

                    if (searchResponse.IsValidResponse)
                    {
                        _logger.LogDebug("Fetched {Count} trades for period {Period} for {poolAddressSetCount} pools", searchResponse.Documents.Count, period.Key, poolAddressSet.Count);
                        if (poolAddressSet.Count < 10)
                        {
                            _logger.LogDebug("poolAddressSet: {Trades}", string.Join(", ", poolAddressSet));
                        }
                        var tradesInPeriod = searchResponse.Documents.Where(t => t.ValueUSD.HasValue);
                        var grouped = tradesInPeriod.GroupBy(t => t.PoolAddress)
                            .ToDictionary(g => g.Key, g => g.Sum(t => t.ValueUSD!.Value));

                        foreach (var kv in grouped)
                        {
                            var poolAddress = kv.Key;
                            var volume = kv.Value;

                            if (!volumes.ContainsKey(poolAddress))
                            {
                                volumes[poolAddress] = (Volume1H: 0m, Volume24H: 0m, Volume7D: 0m);
                            }

                            var current = volumes[poolAddress];
                            volumes[poolAddress] = period.Key switch
                            {
                                "1H" => (volume, current.Volume24H, current.Volume7D),
                                "24H" => (current.Volume1H, volume, current.Volume24H),
                                "7D" => (current.Volume1H, current.Volume24H, volume),
                                _ => current
                            };
                        }
                    }
                    else
                    {
                        _logger.LogError("Elasticsearch query failed for {period}: {Error}", period.Key, searchResponse.DebugInformation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate volumes for {period}", period.Key);
                }
            }

            return volumes;
        }

        private static TradeFilter NormalizeFilter(TradeFilter filter)
        {
            return new TradeFilter
            {
                AssetIdIn = filter.AssetIdIn,
                AssetIdOut = filter.AssetIdOut,
                AssetId = filter.AssetId,
                AssetIdA = filter.AssetIdA,
                AssetIdB = filter.AssetIdB,
                TxId = filter.TxId?.Trim(),
                Trader = filter.Trader?.Trim(),
                PoolAddress = filter.PoolAddress?.Trim(),
                PoolAppId = filter.PoolAppId,
                Protocol = filter.Protocol,
                TradeState = filter.TradeState,
                BlockFrom = filter.BlockFrom,
                BlockTo = filter.BlockTo,
                TimestampFrom = filter.TimestampFrom,
                TimestampTo = filter.TimestampTo,
                MinValueUSD = filter.MinValueUSD,
                MaxValueUSD = filter.MaxValueUSD,
                MinFeesUSD = filter.MinFeesUSD,
                MaxFeesUSD = filter.MaxFeesUSD,
                MinAmountIn = filter.MinAmountIn,
                MaxAmountIn = filter.MaxAmountIn,
                MinAmountOut = filter.MinAmountOut,
                MaxAmountOut = filter.MaxAmountOut,
                SortBy = filter.SortBy?.Trim(),
                SortDirection = filter.SortDirection?.Trim(),
                Offset = Math.Max(filter.Offset, 0),
                Size = Math.Clamp(filter.Size, 1, 500)
            };
        }

        private static PagedResult<Trade> CreatePagedResult(IEnumerable<Trade> trades, long total, TradeFilter filter)
        {
            return new PagedResult<Trade>
            {
                Items = trades,
                Total = total,
                Offset = filter.Offset,
                Size = filter.Size,
                HasMore = filter.Offset + filter.Size < total
            };
        }

        private static SortOrder GetSortOrder(TradeFilter filter)
        {
            return string.Equals(filter.SortDirection, "asc", StringComparison.OrdinalIgnoreCase)
                ? SortOrder.Asc
                : SortOrder.Desc;
        }

        private static Elastic.Clients.Elasticsearch.SortOptionsDescriptor<Trade> BuildSort(
            Elastic.Clients.Elasticsearch.SortOptionsDescriptor<Trade> sort,
            TradeFilter filter)
        {
            var order = GetSortOrder(filter);

            return filter.SortBy?.ToLowerInvariant() switch
            {
                "timestamp" => sort.Field(f => f.Field(t => t.Timestamp).Order(order)),
                "valueusd" => sort.Field(f => f.Field(t => t.ValueUSD).Order(order)),
                "feesusd" => sort.Field(f => f.Field(t => t.FeesUSD).Order(order)),
                "assetamountin" => sort.Field(f => f.Field(t => t.AssetAmountIn).Order(order)),
                "assetamountout" => sort.Field(f => f.Field(t => t.AssetAmountOut).Order(order)),
                _ => sort.Field(f => f.Field(t => t.BlockId).Order(order))
            };
        }

        internal static Query BuildQuery(QueryDescriptor<Trade> query, TradeFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.TxId))
            {
                return query.Term(t => t.Field("txId.keyword").Value(FieldValue.String(filter.TxId)));
            }

            var must = new List<Action<QueryDescriptor<Trade>>>();

            if (filter.AssetIdIn.HasValue && filter.AssetIdOut.HasValue)
            {
                must.Add(m => m.Term(t => t.Field(f => f.AssetIdIn).Value(filter.AssetIdIn.Value)));
                must.Add(m => m.Term(t => t.Field(f => f.AssetIdOut).Value(filter.AssetIdOut.Value)));
            }
            else if (filter.AssetIdIn.HasValue || filter.AssetIdOut.HasValue)
            {
                AddEitherAssetClause(must, filter.AssetIdIn ?? filter.AssetIdOut!.Value);
            }

            if (filter.AssetId.HasValue)
            {
                AddEitherAssetClause(must, filter.AssetId.Value);
            }

            if (filter.AssetIdA.HasValue && filter.AssetIdB.HasValue)
            {
                AddEitherAssetClause(must, filter.AssetIdA.Value);
                AddEitherAssetClause(must, filter.AssetIdB.Value);
            }
            else if (filter.AssetIdA.HasValue || filter.AssetIdB.HasValue)
            {
                AddEitherAssetClause(must, filter.AssetIdA ?? filter.AssetIdB!.Value);
            }

            if (!string.IsNullOrWhiteSpace(filter.Trader))
            {
                must.Add(m => m.Term(t => t.Field("trader.keyword").Value(FieldValue.String(filter.Trader))));
            }

            if (!string.IsNullOrWhiteSpace(filter.PoolAddress))
            {
                must.Add(m => m.Term(t => t.Field("poolAddress.keyword").Value(FieldValue.String(filter.PoolAddress))));
            }

            if (filter.PoolAppId.HasValue)
            {
                must.Add(m => m.Term(t => t.Field(f => f.PoolAppId).Value(filter.PoolAppId.Value)));
            }

            if (filter.Protocol.HasValue)
            {
                must.Add(m => m.Term(t => t.Field("protocol.keyword").Value(FieldValue.String(filter.Protocol.Value.ToString()))));
            }

            if (filter.TradeState.HasValue)
            {
                must.Add(m => m.Term(t => t.Field("tradeState.keyword").Value(FieldValue.String(filter.TradeState.Value.ToString()))));
            }

            AddUlongRangeClause(must, f => f.BlockId, filter.BlockFrom, filter.BlockTo);
            AddDateRangeClause(must, f => f.Timestamp, filter.TimestampFrom, filter.TimestampTo);
            AddDecimalRangeClause(must, f => f.ValueUSD, filter.MinValueUSD, filter.MaxValueUSD);
            AddDecimalRangeClause(must, f => f.FeesUSD, filter.MinFeesUSD, filter.MaxFeesUSD);
            AddUlongRangeClause(must, f => f.AssetAmountIn, filter.MinAmountIn, filter.MaxAmountIn);
            AddUlongRangeClause(must, f => f.AssetAmountOut, filter.MinAmountOut, filter.MaxAmountOut);

            return must.Count == 0
                ? query.MatchAll()
                : query.Bool(b => b.Must(must.ToArray()));
        }

        private static void AddEitherAssetClause(List<Action<QueryDescriptor<Trade>>> must, ulong assetId)
        {
            must.Add(m => m.Bool(b => b.Should(
                s => s.Term(t => t.Field(f => f.AssetIdIn).Value(assetId)),
                s => s.Term(t => t.Field(f => f.AssetIdOut).Value(assetId))
            )));
        }

        private static void AddUlongRangeClause(
            List<Action<QueryDescriptor<Trade>>> must,
            System.Linq.Expressions.Expression<Func<Trade, object?>> field,
            ulong? gte,
            ulong? lte)
        {
            if (!gte.HasValue && !lte.HasValue) return;

            must.Add(m => m.Range(r => r.Number(n =>
            {
                if (gte.HasValue && lte.HasValue)
                {
                    n.Field(field).Gte(gte.Value).Lte(lte.Value);
                }
                else if (gte.HasValue)
                {
                    n.Field(field).Gte(gte.Value);
                }
                else if (lte.HasValue)
                {
                    n.Field(field).Lte(lte.Value);
                }
            })));
        }

        private static void AddDecimalRangeClause(
            List<Action<QueryDescriptor<Trade>>> must,
            System.Linq.Expressions.Expression<Func<Trade, object?>> field,
            decimal? gte,
            decimal? lte)
        {
            if (!gte.HasValue && !lte.HasValue) return;

            must.Add(m => m.Range(r => r.Number(n =>
            {
                if (gte.HasValue && lte.HasValue)
                {
                    n.Field(field).Gte((double)gte.Value).Lte((double)lte.Value);
                }
                else if (gte.HasValue)
                {
                    n.Field(field).Gte((double)gte.Value);
                }
                else if (lte.HasValue)
                {
                    n.Field(field).Lte((double)lte.Value);
                }
            })));
        }

        private static void AddDateRangeClause(
            List<Action<QueryDescriptor<Trade>>> must,
            System.Linq.Expressions.Expression<Func<Trade, object?>> field,
            DateTimeOffset? gte,
            DateTimeOffset? lte)
        {
            if (!gte.HasValue && !lte.HasValue) return;

            must.Add(m => m.Range(r => r.Date(d =>
            {
                if (gte.HasValue && lte.HasValue)
                {
                    d.Field(field).Gte(gte.Value.ToString("o")).Lte(lte.Value.ToString("o"));
                }
                else if (gte.HasValue)
                {
                    d.Field(field).Gte(gte.Value.ToString("o"));
                }
                else if (lte.HasValue)
                {
                    d.Field(field).Lte(lte.Value.ToString("o"));
                }
            })));
        }
    }
}
