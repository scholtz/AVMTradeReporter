namespace AVMTradeReporter.Model.Configuration
{
    public class AppConfiguration
    {
        /// <summary>
        /// Gets or sets the unique identifier for the indexer.
        /// </summary>
        public string IndexerId { get; set; } = String.Empty;
        /// <summary>
        /// AVM Algod configuration
        /// </summary>
        public AlgodConfiguration Algod { get; set; } = new AlgodConfiguration();
        
        /// <summary>
        /// Elasticsearch configuration
        /// </summary>
        public ElasticConfiguration Elastic { get; set; } = new ElasticConfiguration();
    }
}
