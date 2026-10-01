using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace AVMTradeReporter.Services.CoinGecko
{
    /// <summary>A block for which every trade / liquidity event is stored and searchable.</summary>
    public sealed record IndexedBlock(ulong Round, long UnixTimestamp);

    /// <summary>
    /// Watermark of fully indexed blocks - the value behind <c>GET /api/coingecko/latest-block</c>.
    /// </summary>
    /// <remarks>
    /// <c>Indexer.Round</c> cannot be used for this: blocks are processed concurrently
    /// (<c>BlockProcessing.MaxConcurrentTasks</c>), so it runs ahead of the blocks whose events are actually stored,
    /// and GeckoTerminal would skip those events for good.
    /// </remarks>
    public interface IIndexedBlockTracker
    {
        /// <summary>Starts the watermark at a round known to be complete (all earlier work is done).</summary>
        void Seed(ulong completedRound, long unixTimestamp);

        /// <summary>Records that a block was fully processed and stored. Out of order completions are fine.</summary>
        void MarkCompleted(ulong round, long? unixTimestamp);

        /// <summary>The newest visible block, from this process or (other replica) from Redis. Null while unknown.</summary>
        Task<IndexedBlock?> GetLatestAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// This process's contiguous completed watermark BEFORE the visibility delay - what the indexer persists as
        /// <c>Indexer.StoredThrough</c>. Null until seeded.
        /// </summary>
        IndexedBlock? CompletedThrough { get; }
    }

    public sealed class IndexedBlockTracker : IIndexedBlockTracker
    {
        private readonly object _lock = new();
        private readonly SortedDictionary<ulong, long> _pending = new();
        private readonly IDatabase? _redis;
        private readonly string _redisKey;
        private readonly TimeSpan _visibilityDelay;
        private readonly ILogger<IndexedBlockTracker> _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private long _persistedRound = -1;
        private IndexedBlock? _watermark;
        private IndexedBlock? _published;
        private (IndexedBlock? Value, DateTimeOffset At)? _redisRead;

        public IndexedBlockTracker(
            IOptions<AppConfiguration> options,
            ILogger<IndexedBlockTracker> logger,
            IServiceProvider services,
            TimeProvider? timeProvider = null)
        {
            var config = options.Value;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _visibilityDelay = TimeSpan.FromSeconds(Math.Max(0, config.CoinGecko.LatestBlockVisibilityDelaySeconds));
            _redis = config.Redis.Enabled ? services.GetService<IDatabase>() : null;
            _redisKey = $"{config.Redis.EnvironmentKeyPrefix}coingecko:latest-block";
        }

        public IndexedBlock? CompletedThrough
        {
            get { lock (_lock) return _watermark; }
        }

        public void Seed(ulong completedRound, long unixTimestamp)
        {
            lock (_lock)
            {
                if (_watermark != null && _watermark.Round >= completedRound) return;
                _watermark = new IndexedBlock(completedRound, unixTimestamp);
                _published = _watermark;
                foreach (var stale in _pending.Keys.Where(k => k <= completedRound).ToList()) _pending.Remove(stale);
            }
            AdvancePending();
            PersistAsync(_published!).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        public void MarkCompleted(ulong round, long? unixTimestamp)
        {
            lock (_lock)
            {
                if (_watermark == null || round <= _watermark.Round) return; // not seeded (e.g. backward indexer) or already covered
                _pending[round] = unixTimestamp ?? _watermark.UnixTimestamp;
            }
            AdvancePending();
        }

        private void AdvancePending()
        {
            List<IndexedBlock> advanced = new();
            lock (_lock)
            {
                if (_watermark == null) return;
                while (_pending.TryGetValue(_watermark.Round + 1, out var ts))
                {
                    _pending.Remove(_watermark.Round + 1);
                    _watermark = new IndexedBlock(_watermark.Round + 1, ts);
                    advanced.Add(_watermark);
                }
            }
            // The last advanced block is the new watermark; intermediate ones are obsolete once it is published.
            if (advanced.Count == 0) return;
            var newest = advanced[^1];
            _ = PublishAfterDelayAsync(newest);
        }

        private async Task PublishAfterDelayAsync(IndexedBlock block)
        {
            try
            {
                if (_visibilityDelay > TimeSpan.Zero) await Task.Delay(_visibilityDelay, _time);
                lock (_lock)
                {
                    if (_published != null && _published.Round >= block.Round) return;
                    _published = block;
                }
                await PersistAsync(block);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not publish latest indexed block {round}", block.Round);
            }
        }

        private async Task PersistAsync(IndexedBlock block)
        {
            if (_redis == null) return;
            // One write at a time and never an older block than the last one written: two overlapping publish tasks
            // (blocks come every ~2.8 s, the visibility delay is ~3 s) must not leave Redis - which other replicas
            // read - on the older value.
            await _persistLock.WaitAsync();
            try
            {
                if ((long)block.Round <= _persistedRound) return;
                // A restarting / second indexing pod may seed below what another pod already published: never regress it.
                var current = await _redis.StringGetAsync(_redisKey);
                if (current.HasValue && JsonSerializer.Deserialize<IndexedBlock>(current.ToString()) is { } existing && existing.Round >= block.Round)
                {
                    _persistedRound = (long)existing.Round;
                    return;
                }
                await _redis.StringSetAsync(_redisKey, JsonSerializer.Serialize(block), TimeSpan.FromHours(6));
                _persistedRound = (long)block.Round;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not mirror latest indexed block to Redis");
            }
            finally
            {
                _persistLock.Release();
            }
        }

        public async Task<IndexedBlock?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_published != null) return _published;
            }
            if (_redis == null) return null;

            // Replica that does not index (or has not yet seen a block): the indexing replica mirrors the value to Redis.
            var now = _time.GetUtcNow();
            if (_redisRead is { } cached && now - cached.At < TimeSpan.FromSeconds(1)) return cached.Value;
            IndexedBlock? value = null;
            try
            {
                var raw = await _redis.StringGetAsync(_redisKey);
                if (raw.HasValue) value = JsonSerializer.Deserialize<IndexedBlock>(raw.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read latest indexed block from Redis");
            }
            _redisRead = (value, now);
            return value;
        }
    }
}
