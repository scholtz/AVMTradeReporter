namespace AVMTradeReporter.Repository
{
    /// <summary>
    /// Per-document outcome of a bulk store. <see cref="Reached"/> is false when Elasticsearch could not be asked at all
    /// (transport error, invalid response): nothing is known about any document and the caller keeps everything. When it
    /// was reached, every document not in <see cref="RejectedIds"/> is persisted; a rejected one (a mapping conflict, a
    /// malformed document) is retried by the caller a bounded number of times - it will mostly be rejected again.
    /// A deployment without Elasticsearch, and an empty batch, report <see cref="AllStored"/>: nothing is left to persist.
    /// </summary>
    public sealed record StoreResult(bool Reached, IReadOnlyCollection<string> RejectedIds)
    {
        public static readonly StoreResult AllStored = new(true, Array.Empty<string>());
        public static readonly StoreResult Unreachable = new(false, Array.Empty<string>());

        public bool AllAccepted => Reached && RejectedIds.Count == 0;
    }
}
