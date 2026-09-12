using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DEXProtocol
    {
        Pact,
        Tiny,
        Biatec,
        /// <summary>
        /// Pool flagged as a scam deployment (ScamRating above 80). Once assigned it is sticky: trades,
        /// liquidity events and pool processors must never overwrite it with the protocol the pool imitates.
        /// </summary>
        Scam
    }
}
