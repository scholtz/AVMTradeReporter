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

        /// <summary>Documents per bulk request: a backlog that grew during an outage is sent in slices Elasticsearch accepts.</summary>
        public const int BulkChunkSize = 2000;

        /// <summary>While Elasticsearch is unreachable the (growing) backlog is re-tried this often, not with every block.</summary>
        public static readonly TimeSpan DefaultUnreachableRetryInterval = TimeSpan.FromSeconds(10);

        private readonly TimeSpan _unreachableRetryInterval;

        public PendingBlockBatches(TimeSpan? unreachableRetryInterval = null)
        {
            _unreachableRetryInterval = unreachableRetryInterval ?? DefaultUnreachableRetryInterval;
        }

        private DateTimeOffset _unreachableSince = DateTimeOffset.MinValue;
        private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;

        private readonly ConcurrentDictionary<ulong, Batch> _pending = new();
        private readonly ConcurrentDictionary<string, int> _rejections = new();
        // Sent at least once and not settled yet, per document type: the two stores of a flush run concurrently, each
        // touches only its own set (and only inside the flush lock).
        private readonly HashSet<string> _sentTrades = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sentLiquidity = new(StringComparer.Ordinal);
        private volatile bool _needsRetry;
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        /// <param name="Completed">Blocks whose documents are now all settled (stored, or dropped after repeated rejection).</param>
        /// <param name="Unreachable">A store could not reach Elasticsearch: those documents stay pending for the next flush.</param>
        /// <param name="AbandonedDocuments">Tx ids Elasticsearch rejected on every attempt: dropped, their events are lost (logged by the caller).</param>
        /// <param name="BackingOff">No attempt was made: Elasticsearch was unreachable a moment ago and the backlog is re-tried only every <see cref="DefaultUnreachableRetryInterval"/>.</param>
        public sealed record FlushResult(IReadOnlyList<(ulong Round, long? Timestamp)> Completed, bool Unreachable, IReadOnlyList<string> AbandonedDocuments, bool BackingOff = false);

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
        /// The last flush left closed batches behind (Elasticsearch unreachable, or documents it rejected): they wait for
        /// another flush, which a block without documents (see <see cref="TryCompleteEmpty"/>) would not trigger by itself.
        /// </summary>
        public bool NeedsRetry => _needsRetry;

        /// <summary>
        /// Completes a closed block that registered no document, without a flush: there is nothing to store, so it must not
        /// queue behind the flush lock - which is held for a whole Elasticsearch refresh (the bulk requests wait for it)
        /// and is what limited a catch-up to a handful of blocks per second, most of them empty. False when the block has
        /// documents (it needs <see cref="FlushAsync"/>), is not closed, or was completed already.
        /// </summary>
        public bool TryCompleteEmpty(ulong round, out long? timestamp)
        {
            timestamp = null;
            if (!_pending.TryGetValue(round, out var batch) || !batch.Closed || !batch.Trades.IsEmpty || !batch.Liquidity.IsEmpty) return false;
            // exactly this batch: a concurrent flush that saw it closed and empty removes it the same way, one of the two wins
            if (!_pending.TryRemove(new KeyValuePair<ulong, Batch>(round, batch))) return false;
            timestamp = batch.Timestamp;
            return true;
        }

        /// <summary>
        /// Stores the documents of every closed batch (documents sent for the first time and re-sent ones separately, so only
        /// the former are announced), settles them per document and returns the blocks whose documents are thereby all
        /// settled. Flushes are serialized: block tasks finish concurrently, and the second flush must only see what the
        /// first one left behind.
        /// </summary>
        /// <param name="onBehalfOf">
        /// The block whose task asks for the flush. Flushes queue on one lock and each stores every closed batch, so by the
        /// time a queued caller gets the lock an earlier flush has usually stored its block already (group commit): it then
        /// returns at once instead of holding its slot for another whole refresh on behalf of blocks that have their own
        /// task waiting right behind it. Null: flush whatever is closed (retries, shutdown).
        /// </param>
        public async Task<FlushResult> FlushAsync(
            Func<Trade[], bool, Task<StoreResult>> storeTrades,
            Func<Liquidity[], bool, Task<StoreResult>> storeLiquidity,
            CancellationToken cancellationToken,
            int maxRejections = DefaultMaxRejections,
            ulong? onBehalfOf = null)
        {
            await _flushLock.WaitAsync(cancellationToken);
            try
            {
                var none = Array.Empty<(ulong, long?)>();
                if (onBehalfOf is { } round && !_pending.ContainsKey(round)) return new FlushResult(none, false, Array.Empty<string>());
                var closed = _pending.Where(kv => kv.Value.Closed).ToArray();
                if (closed.Length == 0)
                {
                    _needsRetry = false;
                    return new FlushResult(none, false, Array.Empty<string>());
                }

                var abandoned = new List<string>();
                var now = DateTimeOffset.UtcNow;
                if (_unreachableSince != DateTimeOffset.MinValue && now - _lastAttemptAt < _unreachableRetryInterval)
                {
                    // Elasticsearch was unreachable a moment ago: do not re-send the whole backlog with every block
                    return new FlushResult(none, false, abandoned, BackingOff: true);
                }
                _lastAttemptAt = now;
                // The two indices are stored at the same time: each bulk waits for an Elasticsearch refresh (up to a second),
                // one after the other doubled the time every waiting block spends behind the flush lock.
                var abandonedLiquidity = new List<string>();
                var tradesTask = StoreAsync(closed, b => b.Trades, t => t.TxId, storeTrades, maxRejections, abandoned, _sentTrades);
                var liquidityTask = StoreAsync(closed, b => b.Liquidity, l => l.TxId, storeLiquidity, maxRejections, abandonedLiquidity, _sentLiquidity);
                await Task.WhenAll(tradesTask, liquidityTask);
                var unreachable = !await tradesTask | !await liquidityTask;
                abandoned.AddRange(abandonedLiquidity);
                _unreachableSince = unreachable ? (_unreachableSince == DateTimeOffset.MinValue ? now : _unreachableSince) : DateTimeOffset.MinValue;

                if (cancellationToken.IsCancellationRequested)
                {
                    _needsRetry = true; // nothing was completed: the closed batches still wait
                    return new FlushResult(none, unreachable, abandoned);
                }

                var completed = new List<(ulong, long?)>(closed.Length);
                var leftBehind = false;
                foreach (var kv in closed)
                {
                    if (!kv.Value.Trades.IsEmpty || !kv.Value.Liquidity.IsEmpty)
                    {
                        leftBehind = true;
                        continue;
                    }
                    // exactly this batch (see TryCompleteEmpty, which may have completed it meanwhile)
                    if (_pending.TryRemove(kv)) completed.Add((kv.Key, kv.Value.Timestamp));
                }
                // Decided only now, never raised while the flush runs: every empty block completing meanwhile would see it
                // and queue behind this very flush - the wait the fast path exists to avoid.
                _needsRetry = leftBehind;
                return new FlushResult(completed, unreachable, abandoned);
            }
            catch
            {
                _needsRetry = true; // a store threw: the closed batches still wait
                throw;
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
            List<string> abandoned,
            HashSet<string> sent)
        {
            var all = closed.SelectMany(kv => documents(kv.Value).Values).ToArray();
            if (all.Length == 0) return true;
            var reached = true;
            var (fresh, resent) = FirstSend.Split(all, idOf, sent);
            foreach (var (group, publish) in new[] { (fresh, true), (resent, false) })
            {
                foreach (var docs in group.Chunk(BulkChunkSize))
                {
                    if (!reached) break; // the store is down: no point sending the remaining slices now
                    var result = await store(docs, publish);
                    foreach (var d in docs) sent.Add(idOf(d));
                    if (!result.Reached)
                    {
                        reached = false;
                        continue;
                    }
                    var covered = docs.Select(idOf).ToHashSet(StringComparer.Ordinal);
                    Settle(closed, documents, covered, result.RejectedIds, maxRejections, abandoned, sent);
                }
            }
            return reached;
        }

        private void Settle<T>(KeyValuePair<ulong, Batch>[] closed, Func<Batch, ConcurrentDictionary<string, T>> documents, IReadOnlySet<string> covered, IReadOnlyCollection<string> rejectedIds, int maxRejections, List<string> abandoned, HashSet<string> sent)
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
                        Forget(docs, txId, sent);
                        continue;
                    }
                    if (_rejections.AddOrUpdate(txId, 1, (_, count) => count + 1) >= Math.Max(1, maxRejections))
                    {
                        Forget(docs, txId, sent);
                        abandoned.Add(txId);
                    }
                }
            }
        }

        private void Forget<T>(ConcurrentDictionary<string, T> docs, string txId, HashSet<string> sent)
        {
            docs.TryRemove(txId, out _);
            _rejections.TryRemove(txId, out _);
            sent.Remove(txId);
        }
    }
}
