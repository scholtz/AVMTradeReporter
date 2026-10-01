using AVMTradeReporter.Models.Data;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// The forward indexer's not-yet-stored documents, kept per block so that "block N is stored" is decided by the fate of
    /// block N's own documents (blocks are processed concurrently, so a shared bag would mix them). A batch is closed once
    /// every transaction of its block has been registered; only closed batches are flushed, and a batch whose store failed
    /// stays pending and goes out with the next flush (stores are idempotent upserts by tx id).
    /// </summary>
    public sealed class PendingBlockBatches
    {
        private sealed class Batch
        {
            public readonly ConcurrentDictionary<string, Trade> Trades = new();
            public readonly ConcurrentDictionary<string, Liquidity> Liquidity = new();
            public long? Timestamp;
            public volatile bool Closed;
        }

        private readonly ConcurrentDictionary<ulong, Batch> _pending = new();
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        /// <param name="Completed">Blocks whose documents are now all acknowledged.</param>
        /// <param name="StoreFailed">A store call answered false: the closed batches stay pending for the next flush.</param>
        public sealed record FlushResult(IReadOnlyList<(ulong Round, long? Timestamp)> Completed, bool StoreFailed);

        public void Add(Trade trade) => _pending.GetOrAdd(trade.BlockId, _ => new Batch()).Trades[trade.TxId] = trade;

        public void Add(Liquidity liquidity) => _pending.GetOrAdd(liquidity.BlockId, _ => new Batch()).Liquidity[liquidity.TxId] = liquidity;

        /// <summary>Starts (or finds) the batch of a block that is being processed, so an empty block completes like any other.</summary>
        public void Open(ulong round, long? timestamp)
        {
            var batch = _pending.GetOrAdd(round, _ => new Batch());
            if (timestamp != null) batch.Timestamp = timestamp;
        }

        /// <summary>All transactions of the block were registered (or processing gave up): the batch may be flushed.</summary>
        public void Close(ulong round) => _pending.GetOrAdd(round, _ => new Batch()).Closed = true;

        /// <summary>Forgets a block that will never be stored (it could not be fetched at all).</summary>
        public void Drop(ulong round) => _pending.TryRemove(round, out _);

        public int PendingCount => _pending.Count;

        /// <summary>
        /// Stores the documents of every closed batch (one bulk call per index) and returns the blocks that are thereby
        /// completely stored. Each index's documents are forgotten as soon as that index acknowledged them; a block completes
        /// only when both did. A failed store leaves the batches pending for the next flush. Flushes are serialized: block
        /// tasks finish concurrently, and the second flush must only see what the first one left behind, not store (and
        /// publish to the hub) the same documents twice.
        /// </summary>
        public async Task<FlushResult> FlushAsync(
            Func<Trade[], Task<bool>> storeTrades,
            Func<Liquidity[], Task<bool>> storeLiquidity,
            CancellationToken cancellationToken)
        {
            await _flushLock.WaitAsync(cancellationToken);
            try
            {
                var closed = _pending.Where(kv => kv.Value.Closed).ToArray();
                if (closed.Length == 0) return new FlushResult(Array.Empty<(ulong, long?)>(), false);

                var trades = closed.SelectMany(kv => kv.Value.Trades.Values).ToArray();
                var tradesOk = trades.Length == 0 || await storeTrades(trades);
                if (tradesOk) foreach (var kv in closed) kv.Value.Trades.Clear();

                var liquidity = closed.SelectMany(kv => kv.Value.Liquidity.Values).ToArray();
                var liquidityOk = liquidity.Length == 0 || await storeLiquidity(liquidity);
                if (liquidityOk) foreach (var kv in closed) kv.Value.Liquidity.Clear();

                if (!tradesOk || !liquidityOk) return new FlushResult(Array.Empty<(ulong, long?)>(), true);
                if (cancellationToken.IsCancellationRequested) return new FlushResult(Array.Empty<(ulong, long?)>(), false);

                var completed = new List<(ulong, long?)>(closed.Length);
                foreach (var kv in closed)
                {
                    if (_pending.TryRemove(kv.Key, out var batch)) completed.Add((kv.Key, batch.Timestamp));
                }
                return new FlushResult(completed, false);
            }
            finally
            {
                _flushLock.Release();
            }
        }
    }
}
