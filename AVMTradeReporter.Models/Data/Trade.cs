using AVMTradeReporter.Models.Data.Enums;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Represents a swap/trade event on a DEX pool.
    /// Amount fields are expressed in on-chain base units (not decimal-adjusted).
    /// </summary>
    public class Trade
    {
        /// <summary>
        /// Asset id of the input asset (0 represents ALGO).
        /// </summary>
        public ulong AssetIdIn { get; set; }

        /// <summary>
        /// Asset id of the output asset (0 represents ALGO).
        /// </summary>
        public ulong AssetIdOut { get; set; }

        /// <summary>
        /// Amount of <see cref="AssetIdIn"/> paid by the trader (base units).
        /// </summary>
        public ulong AssetAmountIn { get; set; }

        /// <summary>
        /// Amount of <see cref="AssetIdOut"/> received by the trader (base units).
        /// </summary>
        public ulong AssetAmountOut { get; set; }

        /// <summary>
        /// Tx id of the on-chain application call that produced this trade.
        /// </summary>
        public string TxId { get; set; } = string.Empty;

        /// <summary>
        /// Block round number.
        /// </summary>
        public ulong BlockId { get; set; }

        /// <summary>
        /// Base64-encoded transaction group id.
        /// </summary>
        public string TxGroup { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp of the block containing the transaction.
        /// </summary>
        public DateTimeOffset? Timestamp { get; set; }

        /// <summary>
        /// DEX protocol that emitted this event.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol Protocol { get; set; }

        /// <summary>
        /// Trader address in human-readable format.
        /// </summary>
        public string Trader { get; set; } = string.Empty;

        /// <summary>
        /// Pool escrow address in human-readable format.
        /// </summary>
        public string PoolAddress { get; set; } = string.Empty;

        /// <summary>
        /// Pool application id.
        /// </summary>
        public ulong PoolAppId { get; set; }

        /// <summary>
        /// Top-level transaction id associated with the group (used for correlation).
        /// </summary>
        public string TopTxId { get; set; } = string.Empty;

        /// <summary>
        /// Persistence state of this record (pool vs confirmed).
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TxState TradeState { get; set; } = TxState.TxPool;

        /// <summary>
        /// Pool reserve A after the swap (base units).
        /// </summary>
        public ulong A { get; set; }

        /// <summary>
        /// Pool reserve B after the swap (base units).
        /// </summary>
        public ulong B { get; set; }

        /// <summary>
        /// Pool liquidity value after the swap (protocol-specific, base units).
        /// </summary>
        public ulong L { get; set; }

        /// <summary>
        /// Protocol fees in asset A (base units), if available.
        /// </summary>
        public ulong? AF { get; set; }

        /// <summary>
        /// Protocol fees in asset B (base units), if available.
        /// </summary>
        public ulong? BF { get; set; }

        /// <summary>
        /// USD valuation of the trade.
        /// Uses current asset USD prices (derived from trusted pools) at time of processing.
        /// When both sides have a price, valuation is the average of both sides.
        /// When only one side has a price, valuation is based on the priced side.
        /// </summary>
        public decimal? ValueUSD { get; set; }

        /// <summary>
        /// Trade price expressed as USD per one unit of the canonical base asset.
        /// Canonical base asset is defined as <c>min(AssetIdIn, AssetIdOut)</c>.
        /// This keeps the reported USD price stable regardless of swap direction.
        /// </summary>
        public decimal? PriceUSD { get; set; }

        /// <summary>
        /// Gross fee collected for this trade valued in USD.
        /// Calculated from the input side only as:
        /// <c>(AssetAmountIn converted to decimal) × (input asset USD price) × (pool LPFee)</c>.
        /// This is the total fee paid by the trader (LP providers + protocol).
        /// </summary>
        public decimal? FeesUSD { get; set; }

        /// <summary>
        /// Portion of <see cref="FeesUSD"/> collected by LP providers (USD).
        /// Calculated as <c>FeesUSD - FeesUSDProtocol</c>.
        /// </summary>
        public decimal? FeesUSDProvider { get; set; }

        /// <summary>
        /// Portion of <see cref="FeesUSD"/> collected by the protocol owner (USD).
        /// Calculated as <c>FeesUSD × pool.ProtocolFeePortion</c>.
        /// </summary>
        public decimal? FeesUSDProtocol { get; set; }
    }
}
