public class ElasticConfiguration
{
    /// <summary>
    /// Elasticsearch Host URL
    /// </summary>
    public string Host { get; set; } = "http://localhost:9200";

    /// <summary>
    /// API Key for Elasticsearch authentication (optional)
    /// </summary>
    public string? ApiKey { get; set; }
}