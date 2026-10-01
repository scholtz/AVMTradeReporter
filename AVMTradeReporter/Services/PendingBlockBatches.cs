using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// The forward indexer's not-yet-stored documents, kept per block so that "block N is stored" is decided by the fate of
    /// block N's own documents (blocks are processed concurrently, so a shared bag would mix them). A batch is closed once
    /// every transaction of its block has been registered; only closed batches are flushed. Settlement is per document:
    /// what Elasticsearch accepted is forgotten, what it rejected is retried on the next flushes a bounded number of times
    /// and then dropped (a document it rejects for good must not hold the watermark for ever), and while Elasticsearch
    /// cannot be reached at all nothing is dropped, however long it takes. A document is announced to the live feed with
    /// its first send only; re-sends pass <c>publish = false</c>.
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

        public const int DefaultMaxRejections = 3;

        private readonly ConcurrentDictionary<ulong, Batch> _pending = new();
        private readonly ConcurrentDictionary<string, int> _rejections = new();
        private readonly HashSet<string> _sent = new(StringComparer.Ordinal); // guarded by _flushLock
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        /// <param name="Completed">Blocks whose documents are now all settled (stored, or dropped after repeated rejection).</param>
        /// <param name="Unreachable">A store could not reach Elasticsearch: those documents stay pending for the next flush.</param>
        /// <param name="AbandonedDocuments">Tx ids Elasticsearch rejected on every attempt: dropped, their events are lost (logged by the caller).</param>
        public sealed record FlushResult(IReadOnlyList<(ulong Round, long? Timestamp)> Completed, bool Unreachable, IReadOnlyList<string> AbandonedDocuments);

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
        /// Stores the documents of every closed batch (documents sent for the first time and re-sent ones separately, so only
        /// the former are announced), settles them per document and returns the blocks whose documents are thereby all
        /// settled. Flushes are serialized: block tasks finish concurrently, and the second flush must only see what the
        /// first one left behind.
        /// </summary>
        public async Task<FlushResult> FlushAsync(
            Func<Trade[], bool, Task<StoreResult>> storeTrades,
            Func<Liquidity[], bool, Task<StoreResult>> storeLiquidity,
            CancellationToken cancellationToken,
            int maxRejections = DefaultMaxRejections)
        {
            await _flushLock.WaitAsync(cancellationToken);
            try
            {
                var none = Array.Empty<(ulong, long?)>();
                var closed = _pending.Where(kv => kv.Value.Closed).ToArray();
                if (closed.Length == 0) return new FlushResult(none, false, Array.Empty<string>());

                var abandoned = new List<string>();
                var unreachable = false;
                unreachable |= !await StoreAsync(closed, b => b.Trades, t => t.TxId, storeTrades, maxRejections, abandoned);
                unreachable |= !await StoreAsync(closed, b => b.Liquidity, l => l.TxId, storeLiquidity, maxRejections, abandoned);

                if (cancellationToken.IsCancellationRequested) return new FlushResult(none, unreachable, abandoned);

                var completed = new List<(ulong, long?)>(closed.Length);
                foreach (var kv in closed)
                {
                    if (!kv.Value.Trades.IsEmpty || !kv.Value.Liquidity.IsEmpty) continue;
                    if (_pending.TryRemove(kv.Key, out var batch)) completed.Add((kv.Key, batch.Timestamp));
                }
                return new FlushResult(completed, unreachable, abandoned);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        /// <returns>False when a store could not reach Elasticsearch.</returns>
        private async Task<bool> StoreAsync<T>(
            KeyValuePair<ulong, Batch>[] closed,
            Func<Batch, ConcurrentDictionary<string, T>> documents,
            Func<T, string> idOf,
            Func<T[], bool, Task<StoreResult>> store,
            int maxRejections,
            List<string> abandoned)
        {
            var all = closed.SelectMany(kv => documents(kv.Value).Values).ToArray();
            if (all.Length == 0) return true;
            var reached = true;
            foreach (var (docs, publish) in new[] { (all.Where(d => !_sent.Contains(idOf(d))).ToArray(), true), (all.Where(d => _sent.Contains(idOf(d))).ToArray(), false) })
            {
                if (docs.Length == 0) continue;
                var result = await store(docs, publish);
                foreach (var d in docs) _sent.Add(idOf(d));
                if (!result.Reached)
                {
                    reached = false;
                    continue;
                }
                var covered = docs.Select(idOf).ToHashSet(StringComparer.Ordinal);
                Settle(closed, documents, covered, result.RejectedIds, maxRejections, abandoned);
            }
            return reached;
        }

        private void Settle<T>(KeyValuePair<ulong, Batch>[] closed, Func<Batch, ConcurrentDictionary<string, T>> documents, IReadOnlySet<string> covered, IReadOnlyCollection<string> rejectedIds, int maxRejections, List<string> abandoned)
        {
            var rejected = rejectedIds as IReadOnlySet<string> ?? rejectedIds.ToHashSet(StringComparer.Ordinal);
            foreach (var kv in closed)
            {
                var docs = documents(kv.Value);
                foreach (var txId in docs.Keys.ToList())
                {
                    if (!covered.Contains(txId)) continue;
                    if (!rejected.Contains(txId))
                    {
                        Forget(docs, txId);
                        continue;
                    }
                    if (_rejections.AddOrUpdate(txId, 1, (_, count) => count + 1) >= Math.Max(1, maxRejections))
                    {
                        Forget(docs, txId);
                        abandoned.Add(txId);
                    }
                }
            }
        }

        private void Forget<T>(ConcurrentDictionary<string, T> docs, string txId)
        {
            docs.TryRemove(txId, out _);
            _rejections.TryRemove(txId, out _);
            _sent.Remove(txId);
        }
    }
}
