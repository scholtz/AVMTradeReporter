namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Durable Elasticsearch envelope for a <see cref="AVMTradeReporter.Model.Data.BiatecAsset"/>
    /// record (index "assets", one document per asset id). Carries the exact same
    /// System.Text.Json payload that the Redis asset cache stores, as an opaque string,
    /// so both durable stores share one serialization format and Elasticsearch dynamic
    /// mapping never conflicts with the asset's own field types. This exists so the
    /// in-memory asset cache survives pod restarts even in environments where Redis is
    /// disabled (production runs Redis.Enabled=false) - without it, every deploy wiped
    /// the token list until live trades re-populated it asset by asset.
    /// </summary>
    public class AssetSnapshot
    {
        /// <summary>
        /// Asset id (ASA index; 0 for the native token). Document id in the "assets" index.
        /// </summary>
        public ulong Id { get; set; }

        /// <summary>
        /// System.Text.Json serialization of the BiatecAsset, identical to the Redis payload.
        /// </summary>
        public string Json { get; set; } = string.Empty;

        /// <summary>
        /// When the asset record was last written (asset timestamp at persist time).
        /// </summary>
        public DateTimeOffset Updated { get; set; }
    }
}
