using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.DTO.OHLC
{
    // Base status wrapper (TradingView expects lowercase 's')
    public class StatusResponse
    {
        [JsonPropertyName("s")] public string S { get; set; } = "ok"; // ok | no_data | error
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public class OHLCConfigDto
    {
        [JsonPropertyName("supports_search")] public bool Supports_Search { get; set; }
        [JsonPropertyName("supports_group_request")] public bool Supports_Group_Request { get; set; }
        [JsonPropertyName("supports_marks")] public bool Supports_Marks { get; set; }
        [JsonPropertyName("supports_timescale_marks")] public bool Supports_Timescale_Marks { get; set; }
        [JsonPropertyName("supports_time")] public bool Supports_Time { get; set; }
        [JsonPropertyName("supported_resolutions")] public string[] Supported_Resolutions { get; set; } = [];
        [JsonPropertyName("exchanges")] public object[] Exchanges { get; set; } = new object[] { new { value = "ALG", name = "Algorand", desc = "Algorand" } };
        [JsonPropertyName("symbols_types")] public object[] Symbols_Types { get; set; } = new object[] { new { name = "crypto", value = "crypto" } };
    }

    public class SymbolDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("ticker")] public string Ticker { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "crypto";
        [JsonPropertyName("session")] public string Session { get; set; } = "24x7";
        [JsonPropertyName("exchange")] public string Exchange { get; set; } = "ALG";
        [JsonPropertyName("listed_exchange")] public string Listed_Exchange { get; set; } = "ALG";
        [JsonPropertyName("timezone")] public string Timezone { get; set; } = "Etc/UTC";
        [JsonPropertyName("format")] public string Format { get; set; } = "price";
        [JsonPropertyName("minmov")] public int Minmov { get; set; } = 1;
        [JsonPropertyName("minmov2")] public int Minmov2 { get; set; } = 0;
        [JsonPropertyName("pricescale")] public int Pricescale { get; set; }
        [JsonPropertyName("has_intraday")] public bool Has_Intraday { get; set; } = true;
        [JsonPropertyName("has_daily")] public bool Has_Daily { get; set; } = true;
        [JsonPropertyName("has_weekly_and_monthly")] public bool Has_Weekly_Monthly { get; set; } = true;
        [JsonPropertyName("has_no_volume")] public bool Has_No_Volume { get; set; } = false;
        [JsonPropertyName("volume_precision")] public int Volume_Precision { get; set; } = 6;
        [JsonPropertyName("supported_resolutions")] public string[] Supported_Resolutions { get; set; } = [];
        [JsonPropertyName("data_status")] public string Data_Status { get; set; } = "streaming";
    }

    public class SearchSymbolDto
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
        [JsonPropertyName("full_name")] public string Full_Name { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("ticker")] public string Ticker { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "crypto";
        [JsonPropertyName("exchange")] public string Exchange { get; set; } = "ALG";
    }

    public class QuoteValueDto
    {
        [JsonPropertyName("ch")] public decimal Ch { get; set; }
        [JsonPropertyName("chp")] public decimal Chp { get; set; }
        [JsonPropertyName("short_name")] public string Short_Name { get; set; } = string.Empty;
        [JsonPropertyName("exchange")] public string Exchange { get; set; } = "ALG";
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("price")] public decimal Price { get; set; }
        [JsonPropertyName("volume")] public decimal Volume { get; set; }
        [JsonPropertyName("bid")] public decimal Bid { get; set; }
        [JsonPropertyName("ask")] public decimal Ask { get; set; }
        [JsonPropertyName("high_price")] public decimal High_Price { get; set; }
        [JsonPropertyName("low_price")] public decimal Low_Price { get; set; }
    }

    public class QuoteEntryDto
    {
        [JsonPropertyName("s")] public string S { get; set; } = "ok";
        [JsonPropertyName("n")] public string N { get; set; } = string.Empty; // symbol name
        [JsonPropertyName("v")] public QuoteValueDto V { get; set; } = new();
    }

    public class QuotesResponseDto : StatusResponse
    {
        [JsonPropertyName("d")] public List<QuoteEntryDto> D { get; set; } = new();
    }

    public class HistoryResponseDto : StatusResponse
    {
        [JsonPropertyName("t")] public List<long>? T { get; set; }
        [JsonPropertyName("o")] public List<decimal>? O { get; set; }
        [JsonPropertyName("h")] public List<decimal>? H { get; set; }
        [JsonPropertyName("l")] public List<decimal>? L { get; set; }
        [JsonPropertyName("c")] public List<decimal>? C { get; set; }
        [JsonPropertyName("v")] public List<decimal>? V { get; set; }
        [JsonPropertyName("nextTime")] public long? NextTime { get; set; }
    }

    // Batch symbol info (optional endpoint for charting library optimization)
    public class SymbolInfoDto
    {
        [JsonPropertyName("symbols")]
        public string[] Symbols { get; set; } = [];
        [JsonPropertyName("tickers")]
        public string[] Tickers { get; set; } = [];
        [JsonPropertyName("description")] public string[] Description { get; set; } = [];
        [JsonPropertyName("type")] public string[] Type { get; set; } = [];
        [JsonPropertyName("exchangeListed")] public string[] ExchangeListed { get; set; } = [];
        [JsonPropertyName("exchangeTraded")] public string[] ExchangeTraded { get; set; } = [];
        [JsonPropertyName("session")] public string[] Session { get; set; } = [];
        [JsonPropertyName("timezone")] public string[] Timezone { get; set; } = [];
        [JsonPropertyName("minmov")] public int[] Minmov { get; set; } = [];
        [JsonPropertyName("pricescale")] public int[] Pricescale { get; set; } = [];
        [JsonPropertyName("has_intraday")] public bool[] HasIntraday { get; set; } = [];
        [JsonPropertyName("supported_resolutions")] public string[][] SupportedResolutions { get; set; } = [];
    }
}
