namespace AVMTradeReporter.Model.Configuration
{
    public class AlgodConfiguration
    {
        /// <summary>
        /// Algod Host. Empty = this algod is not configured. There is deliberately no default: the fallbacks
        /// (<c>Algod2</c> / <c>Algod3</c>) used to default to a public MAINNET algod on every network - an implicit
        /// dependency on mainnet, and on testnet / Voi a node of the wrong chain.
        /// </summary>
        public string Host { get; set; } = string.Empty;
        /// <summary>
        /// Algod Port
        /// </summary>
        public int Port { get; set; } = 443;
        /// <summary>
        /// Api Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;
        /// <summary>
        /// Use custom header for api key if defined
        /// </summary>
        public string Header { get; set; } = string.Empty;
    }

}
