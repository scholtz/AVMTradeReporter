namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Which configured algod to ask first for a block. The algods are tried in their configured order - the first one
    /// is our own node - but an own node is usually not archival: it answers "failed to retrieve information from the
    /// ledger" for everything older than about a thousand rounds. A catch-up that asks it first for every old block pays
    /// a failed request (and a warning with a stack trace) per block.
    /// </summary>
    /// <remarks>
    /// When a node does not keep round R (and a later node returned it), it does not keep the rounds up to R either, so it
    /// is skipped up to R + <see cref="RetryEveryRounds"/>. After that ONE block task probes it again - block tasks run
    /// concurrently, and without that all of them would probe at once - while the others keep skipping it. A probe that
    /// succeeds makes the node the first choice again at once, so the archival fallback (a public service with a rate
    /// limit and a quota) serves exactly the blocks our own node does not keep. A skipped node stays the last resort for
    /// its rounds. Only "the node does not keep this block" may be reported here: a timeout or a 5xx says nothing about
    /// the other rounds and must not move 200 blocks to the fallback.
    /// </remarks>
    public sealed class AlgodBlockAvailability
    {
        /// <summary>A node that does not keep a block is probed again after this many rounds.</summary>
        public const ulong RetryEveryRounds = 200;

        // per algod: 0 = expected to have every block, otherwise the last round it is skipped for
        private readonly long[] _missingThrough;

        public AlgodBlockAvailability(int algodCount)
        {
            _missingThrough = new long[Math.Max(0, algodCount)];
        }

        /// <summary>The algod indexes in the order to ask for <paramref name="round"/>: those expected to have it, then the rest.</summary>
        public IReadOnlyList<int> Order(ulong round)
        {
            var first = new List<int>(_missingThrough.Length);
            List<int>? skipped = null;
            for (var i = 0; i < _missingThrough.Length; i++)
            {
                if (IsSkipped(i, round)) (skipped ??= new List<int>()).Add(i); else first.Add(i);
            }
            if (skipped != null) first.AddRange(skipped);
            return first;
        }

        /// <summary>Algod <paramref name="index"/> does not keep <paramref name="round"/> (another algod returned it).</summary>
        public void MarkMissing(int index, ulong round)
        {
            if (index < 0 || index >= _missingThrough.Length) return;
            var through = Through(round);
            long current;
            while ((current = Interlocked.Read(ref _missingThrough[index])) < through)
            {
                if (Interlocked.CompareExchange(ref _missingThrough[index], through, current) == current) return;
            }
        }

        /// <summary>Algod <paramref name="index"/> returned a block: it is asked in its configured place again.</summary>
        public void MarkAvailable(int index)
        {
            if (index < 0 || index >= _missingThrough.Length) return;
            Interlocked.Exchange(ref _missingThrough[index], 0);
        }

        /// <summary>Forgets everything: at the chain tip every node has the block and the configured order applies.</summary>
        public void Reset()
        {
            for (var i = 0; i < _missingThrough.Length; i++) Interlocked.Exchange(ref _missingThrough[i], 0);
        }

        private bool IsSkipped(int index, ulong round)
        {
            var through = Interlocked.Read(ref _missingThrough[index]);
            if (through == 0) return false;
            if (round <= (ulong)through) return true;
            // The skip distance is over: exactly one caller wins the probe (it extends the skip for everybody else, and
            // MarkAvailable / MarkMissing settle it with the probe's result).
            return Interlocked.CompareExchange(ref _missingThrough[index], Through(round), through) != through;
        }

        private static long Through(ulong round) => (long)Math.Min(round + RetryEveryRounds, long.MaxValue);
    }
}
