namespace AVMTradeReporter.Model.Configuration
{
    public class AlgodConfiguration
    {
        /// <summary>
        /// Algod Host
        /// </summary>
        public string Host { get; set; } = "https://mainnet-api.4160.nodely.dev";
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
