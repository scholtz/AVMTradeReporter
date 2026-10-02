using System.Text.Json.Serialization;

namespace AVMTradeReporter.Services.CoinGecko
{
    // DTOs of the "GeckoTerminal Integration API Standards v0.1" (non-EVM DEX). Amounts and prices are strings
    // (the spec allows number | string) so JavaScript consumers never round a 19+ digit decimal through a double.

    public class CoinGeckoBlock
    {
        [JsonPropertyName("blockNumber")] public ulong BlockNumber { get; set; }

        /// <summary>UNIX timestamp in seconds (no milliseconds).</summary>
        [JsonPropertyName("blockTimestamp")] public long BlockTimestamp { get; set; }
    }

    public class CoinGeckoLatestBlockResponse
    {
        [JsonPropertyName("block")] public CoinGeckoBlock Block { get; set; } = new();
    }

    public class CoinGeckoAsset
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
        [JsonPropertyName("decimals")] public int Decimals { get; set; }

        /// <summary>Decimalized total supply (<c>total / 10^decimals</c>).</summary>
        [JsonPropertyName("totalSupply"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TotalSupply { get; set; }

        [JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Metadata { get; set; }
    }

    public class CoinGeckoAssetResponse
    {
        [JsonPropertyName("asset")] public CoinGeckoAsset Asset { get; set; } = new();
    }

    public class CoinGeckoPair
    {
        /// <summary>Pool application id.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("dexKey")] public string DexKey { get; set; } = string.Empty;
        [JsonPropertyName("asset0Id")] public string Asset0Id { get; set; } = string.Empty;
        [JsonPropertyName("asset1Id")] public string Asset1Id { get; set; } = string.Empty;

        /// <summary>Swap fee in basis points (1 % = 100).</summary>
        [JsonPropertyName("feeBps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public decimal? FeeBps { get; set; }

        [JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Metadata { get; set; }
    }

    public class CoinGeckoPairResponse
    {
        [JsonPropertyName("pair")] public CoinGeckoPair Pair { get; set; } = new();
    }

    public class CoinGeckoReserves
    {
        [JsonPropertyName("asset0")] public string Asset0 { get; set; } = "0";
        [JsonPropertyName("asset1")] public string Asset1 { get; set; } = "0";
    }

    public class CoinGeckoSwapMetadata
    {
        [JsonPropertyName("fees0In"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Fees0In { get; set; }
        [JsonPropertyName("fees1In"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Fees1In { get; set; }
    }

    [JsonDerivedType(typeof(CoinGeckoSwapEvent))]
    [JsonDerivedType(typeof(CoinGeckoJoinExitEvent))]
    public abstract class CoinGeckoEvent
    {
        [JsonPropertyName("block")] public CoinGeckoBlock Block { get; set; } = new();
        [JsonPropertyName("eventType")] public string EventType { get; set; } = string.Empty;
        [JsonPropertyName("txnId")] public string TxnId { get; set; } = string.Empty;
        [JsonPropertyName("txnIndex")] public ulong TxnIndex { get; set; }
        [JsonPropertyName("eventIndex")] public uint EventIndex { get; set; }
        [JsonPropertyName("maker")] public string Maker { get; set; } = string.Empty;
        [JsonPropertyName("pairId")] public string PairId { get; set; } = string.Empty;
        [JsonPropertyName("reserves")] public CoinGeckoReserves Reserves { get; set; } = new();
    }

    public class CoinGeckoSwapEvent : CoinGeckoEvent
    {
        public CoinGeckoSwapEvent() { EventType = "swap"; }

        [JsonPropertyName("asset0In"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Asset0In { get; set; }
        [JsonPropertyName("asset1In"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Asset1In { get; set; }
        [JsonPropertyName("asset0Out"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Asset0Out { get; set; }
        [JsonPropertyName("asset1Out"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Asset1Out { get; set; }

        /// <summary>Price of asset0 quoted in asset1 (decimalized), always &gt; 0.</summary>
        [JsonPropertyName("priceNative")] public string PriceNative { get; set; } = string.Empty;

        [JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CoinGeckoSwapMetadata? Metadata { get; set; }
    }

    public class CoinGeckoJoinExitEvent : CoinGeckoEvent
    {
        [JsonPropertyName("amount0")] public string Amount0 { get; set; } = "0";
        [JsonPropertyName("amount1")] public string Amount1 { get; set; } = "0";
    }

    public class CoinGeckoEventsResponse
    {
        [JsonPropertyName("events")] public List<CoinGeckoEvent> Events { get; set; } = new();
    }
}
