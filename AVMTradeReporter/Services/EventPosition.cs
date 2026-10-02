namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Position of one top-level transaction inside its block. Shared by the whole inner-transaction tree of
    /// that transaction so every swap / liquidity event it produces gets a unique, chronological
    /// <c>(TxnIndex, EventIndex)</c> pair.
    /// </summary>
    public sealed class EventPosition
    {
        private int _next;

        public EventPosition(ulong txnIndex)
        {
            TxnIndex = txnIndex;
        }

        /// <summary>1-based index of the top-level transaction in the block.</summary>
        public ulong TxnIndex { get; }

        /// <summary>Returns 0, 1, 2, ... for the successive events of the transaction.</summary>
        public uint NextEventIndex() => (uint)(Interlocked.Increment(ref _next) - 1);
    }
}
