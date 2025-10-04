namespace AVMTradeReporter.Model.DTO.OHLC
{
    // Base status wrapper to mirror TradingView 's' field usage
    public class StatusResponse
    {
        public string S { get; set; } = "ok"; // ok | no_data | error
        public string? Error { get; set; }
    }

    public class OHLCConfigDto
    {
        public bool Supports_Search { get; set; }
        public bool Supports_Group_Request { get; set; }
        public bool Supports_Marks { get; set; }
        public bool Supports_Timescale_Marks { get; set; }
        public bool Supports_Time { get; set; }
        public string[] Supported_Resolutions { get; set; } = [];
    }

    public class SymbolDto
    {
        public string Name { get; set; } = string.Empty;
        public string Ticker { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = "crypto";
        public string Session { get; set; } = "24x7";
        public string Exchange { get; set; } = "ALG";
        public string Listed_Exchange { get; set; } = "ALG";
        public string Timezone { get; set; } = "Etc/UTC";
        public string Format { get; set; } = "price";
        public int Minmov { get; set; } = 1;
        public int Minmov2 { get; set; } = 0;
        public int Pricescale { get; set; }
        public bool Has_Intraday { get; set; } = true;
        public bool Has_No_Volume { get; set; } = false;
        public int Volume_Precision { get; set; } = 6;
        public string[] Supported_Resolutions { get; set; } = [];
        public string Data_Status { get; set; } = "streaming";
    }

    public class SearchSymbolDto
    {
        public string Symbol { get; set; } = string.Empty;
        public string Full_Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Ticker { get; set; } = string.Empty;
        public string Type { get; set; } = "crypto";
        public string Exchange { get; set; } = "ALG";
    }

    public class QuoteValueDto
    {
        public decimal Ch { get; set; }
        public decimal Chp { get; set; }
        public string Short_Name { get; set; } = string.Empty;
        public string Exchange { get; set; } = "ALG";
        public string Description { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal High_Price { get; set; }
        public decimal Low_Price { get; set; }
    }

    public class QuoteEntryDto
    {
        public string S { get; set; } = "ok";
        public string N { get; set; } = string.Empty; // symbol name
        public QuoteValueDto V { get; set; } = new();
    }

    public class QuotesResponseDto : StatusResponse
    {
        public List<QuoteEntryDto> D { get; set; } = new();
    }

    public class HistoryResponseDto : StatusResponse
    {
        public List<long>? T { get; set; }
        public List<decimal>? O { get; set; }
        public List<decimal>? H { get; set; }
        public List<decimal>? L { get; set; }
        public List<decimal>? C { get; set; }
        public List<decimal>? V { get; set; }
    }
}
