namespace AVMTradeReporter.Repository
{
    /// <summary>Reads Elasticsearch's own answers out of the exceptions the client throws (Program.cs builds it with ThrowExceptions()).</summary>
    public static class ElasticErrors
    {
        /// <summary>
        /// True when the exception (or an inner one) is Elasticsearch's own "index_not_found_exception" answer for
        /// <paramref name="index"/> - the server replied 404, the index does not exist (a network the DEX has no pool / trade on
        /// yet: Voi). Anything else (connection refused, timeout, auth, a 503, a bare 404 from a proxy) is a failed call, not an
        /// answer. The thrown client exception exposes the server error only through its message (EnableDebugMode in Program.cs),
        /// so the text is matched; the status code is checked structurally when the exception has one. PoolIndexMissingTests pin
        /// this against the real client (and ElasticErrorsTests the edge cases). Deliberate trade-off: callers read a missing index as
        /// "nothing stored", so a wiped cluster looks the same on a network that does have data.
        /// </summary>
        public static bool IsIndexNotFound(Exception? ex, string index)
        {
            var token = $"[{index}]";
            for (var depth = 0; ex != null && depth < 8; ex = ex.InnerException, depth++)
            {
                if (ex is Elastic.Transport.TransportException transport && transport.ApiCallDetails?.HttpStatusCode is int status && status != 404)
                    continue; // another status can quote the token without being the answer
                if (ex.Message.Contains("index_not_found_exception", StringComparison.Ordinal)
                    && ex.Message.Contains(token, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
