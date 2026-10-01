namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Documents are announced to the live feed (hub, recent queue) by their first store only. Both the block batches and
    /// the mempool preview buffer split what they send into "never sent before" (announce) and "re-sent" (do not).
    /// </summary>
    internal static class FirstSend
    {
        public static (T[] Fresh, T[] Resent) Split<T>(IEnumerable<T> documents, Func<T, string> idOf, IReadOnlySet<string> sent)
        {
            var fresh = new List<T>();
            var resent = new List<T>();
            foreach (var document in documents)
            {
                (sent.Contains(idOf(document)) ? resent : fresh).Add(document);
            }
            return (fresh.ToArray(), resent.ToArray());
        }
    }
}
