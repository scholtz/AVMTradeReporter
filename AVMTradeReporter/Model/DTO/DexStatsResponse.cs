namespace AVMTradeReporter.Model.DTO
{
    /// <summary>
    /// Aggregated DEX trading statistics for an arbitrary window, formatted for DefiLlama export.
    /// The window covers [<see cref="From"/>, <see cref="To"/>). Defaults to a full day
    /// (To = From + 1 day) when the caller does not request a specific end; an explicit end lets
    /// callers pull sub-day (e.g. hourly) windows, as needed by DefiLlama's v2 adapter model.
    /// Only confirmed trades are included in the aggregation.
    /// </summary>
    public class DexStatsResponse
    {
        /// <summary>DEX protocol identifier (Biatec, Pact, or Tiny).</summary>
        public string Protocol { get; init; } = string.Empty;

        /// <summary>Start of the statistics window (inclusive).</summary>
        public DateTimeOffset From { get; init; }

        /// <summary>End of the statistics window (exclusive; equals From + 1 day unless an explicit end was requested).</summary>
        public DateTimeOffset To { get; init; }

        /// <summary>Total USD volume traded during the window (sum of valueUSD across all confirmed trades).</summary>
        public decimal VolumeUSD { get; init; }

        /// <summary>Total fees collected from all trades in USD (sum of feesUSD).</summary>
        public decimal FeesUSD { get; init; }

        /// <summary>Total fees collected by liquidity providers in USD (sum of feesUSDProvider).</summary>
        public decimal FeesLPUSD { get; init; }

        /// <summary>Total fees collected by the protocol in USD (sum of feesUSDProtocol).</summary>
        public decimal FeesProtocolUSD { get; init; }
    }
}
