using AVMTradeReporter.Models.Data.Enums;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Represents a liquidity modification (add/remove) on a DEX pool.
    /// Amount fields are expressed in on-chain base units (not decimal-adjusted).
    /// </summary>
    public class Liquidity
    {
        /// <summary>
        /// Indicates whether liquidity was deposited or withdrawn.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public LiquidityDirection Direction { get; set; }

        /// <summary>
        /// Asset id of the first pool asset (0 represents ALGO).
        /// </summary>
        public ulong AssetIdA { get; set; }

        /// <summary>
        /// Asset id of the second pool asset (0 represents ALGO).
        /// </summary>
        public ulong AssetIdB { get; set; }

        /// <summary>
        /// Asset id of the LP token minted/burned.
        /// </summary>
        public ulong AssetIdLP { get; set; }

        /// <summary>
        /// Amount of <see cref="AssetIdA"/> transferred in the modification (base units).
        /// </summary>
        public ulong AssetAmountA { get; set; }

        /// <summary>
        /// Amount of <see cref="AssetIdB"/> transferred in the modification (base units).
        /// </summary>
        public ulong AssetAmountB { get; set; }

        /// <summary>
        /// Amount of <see cref="AssetIdLP"/> minted/burned in the modification (base units).
        /// </summary>
        public ulong AssetAmountLP { get; set; }

        /// <summary>
        /// Pool reserve A after the transaction (base units).
        /// </summary>
        public ulong A { get; set; }

        /// <summary>
        /// Pool reserve B after the transaction (base units).
        /// </summary>
        public ulong B { get; set; }

        /// <summary>
        /// Protocol fees in asset A (base units), if available.
        /// </summary>
        public ulong? AF { get; set; }

        /// <summary>
        /// Protocol fees in asset B (base units), if available.
        /// </summary>
        public ulong? BF { get; set; }

        /// <summary>
        /// Pool liquidity value after the transaction (protocol-specific, base units).
        /// </summary>
        public ulong L { get; set; }

        /// <summary>
        /// Tx id of the on-chain application call that produced this liquidity event.
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
        /// Liquidity provider address in human-readable format.
        /// </summary>
        public string LiquidityProvider { get; set; } = string.Empty;

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
        public TxState TxState { get; set; } = TxState.TxPool;

        /// <summary>
        /// USD valuation of the liquidity modification (deposit/withdraw).
        /// Uses current asset USD prices (derived from trusted pools) at time of processing.
        /// When both sides have a price, valuation is the average of both sides.
        /// When only one side has a price, valuation is based on the priced side.
        /// </summary>
        public decimal? ValueUSD { get; set; }
    }
}
