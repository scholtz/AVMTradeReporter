namespace AVMTradeReporter.Services.CoinGecko
{
    /// <summary>Ordering facts of one stored trade / liquidity document.</summary>
    /// <param name="BlockId">Block round.</param>
    /// <param name="TxnIndex">Stored 1-based top-level transaction index, null for documents indexed before it existed.</param>
    /// <param name="EventIndex">Stored event order inside the top-level transaction, null for old documents.</param>
    /// <param name="GroupId">Top-level transaction id (inner events of one transaction share it).</param>
    /// <param name="TieBreak">Document transaction id, for a deterministic order of old documents.</param>
    /// <param name="Kind">0 = swap, 1 = liquidity.</param>
    public readonly record struct EventOrderKey(ulong BlockId, ulong? TxnIndex, uint? EventIndex, string GroupId, string TieBreak, int Kind);

    /// <summary>
    /// Gives every event of a block a unique <c>(txnIndex, eventIndex)</c>, as the GeckoTerminal spec requires
    /// ("txnIndex + eventIndex must be unique per block", events sorted by them).
    /// </summary>
    /// <remarks>
    /// Documents written since the position is persisted keep their real values. Older documents have none, so they
    /// get a deterministic synthetic position (grouped by top-level transaction, ordered by transaction id) placed
    /// after every real transaction of that block - the same answer on every call, which is what an indexer that
    /// re-requests a range needs. A collision (should never happen) is resolved by bumping the event index rather
    /// than emitting a duplicate, because a duplicate halts the whole integration.
    /// </remarks>
    public static class CoinGeckoEventOrdering
    {
        public static EventPositionPair[] AssignPositions(IReadOnlyList<EventOrderKey> keys)
        {
            var result = new EventPositionPair[keys.Count];
            foreach (var block in Enumerable.Range(0, keys.Count).GroupBy(i => keys[i].BlockId))
            {
                var indices = block.ToList();
                var real = indices.Where(i => keys[i].TxnIndex.HasValue).ToList();
                var legacy = indices.Where(i => !keys[i].TxnIndex.HasValue).ToList();
                var taken = new HashSet<(ulong, uint)>();

                ulong maxReal = 0;
                foreach (var i in real.OrderBy(i => keys[i].TxnIndex!.Value)
                             .ThenBy(i => keys[i].EventIndex ?? uint.MaxValue)
                             .ThenBy(i => keys[i].Kind)
                             .ThenBy(i => keys[i].TieBreak, StringComparer.Ordinal))
                {
                    var txn = keys[i].TxnIndex!.Value;
                    var ev = keys[i].EventIndex ?? 0;
                    while (!taken.Add((txn, ev))) ev++;
                    result[i] = new EventPositionPair(txn, ev);
                    if (txn > maxReal) maxReal = txn;
                }

                var groups = legacy.GroupBy(i => keys[i].GroupId, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                for (var g = 0; g < groups.Count; g++)
                {
                    var txn = maxReal + 1 + (ulong)g;
                    uint ev = 0;
                    foreach (var i in groups[g].OrderBy(i => keys[i].Kind).ThenBy(i => keys[i].TieBreak, StringComparer.Ordinal))
                    {
                        result[i] = new EventPositionPair(txn, ev++);
                    }
                }
            }
            return result;
        }
    }
}
