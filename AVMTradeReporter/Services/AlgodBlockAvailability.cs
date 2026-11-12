namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Which configured algod to ask first for a block. The algods are tried in their configured order - the first one
    /// is our own node - but an own node is usually not archival: it answers "failed to retrieve information from the
    /// ledger" for everything older than about a thousand rounds. A catch-up that asks it first for every old block pays
    /// a failed request (and a warning with a stack trace) per block.
    /// </summary>
    /// <remarks>
    /// When a node could not return round R while a later node could, it does not have the rounds up to R either, so it
    /// is skipped up to R + <see cref="RetryEveryRounds"/>: it is tried again every that many rounds and is the first
    /// choice again as soon as the catch-up reaches the blocks it keeps. The archival fallback (a public service with a
    /// rate limit and a quota) is therefore used for exactly the blocks our own node does not have. A skipped node is
    /// still the last resort for its rounds. Not thread safe by design beyond single reads / writes of a round: the
    /// worst a race does is one extra failed request.
    /// </remarks>
    public sealed class AlgodBlockAvailability
    {
        /// <summary>A node that lacked a block is asked again after this many rounds.</summary>
        public const ulong RetryEveryRounds = 200;

        private readonly long[] _missingThrough;

        public AlgodBlockAvailability(int algodCount)
        {
            _missingThrough = new long[Math.Max(0, algodCount)];
        }

        /// <summary>The algod indexes in the order to ask for <paramref name="round"/>: those expected to have it, then the rest.</summary>
        public IReadOnlyList<int> Order(ulong round)
        {
            var order = new List<int>(_missingThrough.Length);
            for (var i = 0; i < _missingThrough.Length; i++) if (!IsSkipped(i, round)) order.Add(i);
            for (var i = 0; i < _missingThrough.Length; i++) if (IsSkipped(i, round)) order.Add(i);
            return order;
        }

        /// <summary>Algod <paramref name="index"/> could not return <paramref name="round"/>, but another one could.</summary>
        public void MarkMissing(int index, ulong round)
        {
            if (index < 0 || index >= _missingThrough.Length) return;
            var through = (long)Math.Min(round + RetryEveryRounds, long.MaxValue);
            if (through > Interlocked.Read(ref _missingThrough[index])) Interlocked.Exchange(ref _missingThrough[index], through);
        }

        /// <summary>Forgets everything: at the chain tip every node has the block and the configured order applies.</summary>
        public void Reset()
        {
            for (var i = 0; i < _missingThrough.Length; i++) Interlocked.Exchange(ref _missingThrough[i], 0);
        }

        private bool IsSkipped(int index, ulong round) => round <= (ulong)Interlocked.Read(ref _missingThrough[index]);
    }
}
