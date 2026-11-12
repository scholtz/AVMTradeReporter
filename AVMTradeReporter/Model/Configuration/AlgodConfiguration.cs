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
        /// <summary>
        /// Block requests per second the indexer sends to this algod, at most (see <c>AlgodRateLimiter</c>). The default
        /// suits a public service: Nodely's free tier answers HTTP 429 above 50 requests per second per IP, and every
        /// network deployed in the cluster shares one egress IP. A catch-up runs 16 block tasks at once and would cross
        /// that line; a rejected fetch is retries, then the 30 minute lost-block path, then lost events. Set 0 (no limit)
        /// for an own node - explicitly, next to its host: a limit that depended on a default would silently stop
        /// applying when the host is pointed at a public service.
        /// </summary>
        public double MaxRequestsPerSecond { get; set; } = 25;
    }

}
