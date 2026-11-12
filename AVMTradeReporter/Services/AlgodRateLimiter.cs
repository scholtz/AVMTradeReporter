namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Spaces the block requests sent to ONE algod so that they never exceed its rate limit: every request - the first
    /// attempt, a retry, a probe, the lost-block recovery - reserves the next free slot, <c>1 / maxRequestsPerSecond</c>
    /// after the previous one. There is no burst allowance on purpose: a public algod (Nodely's free tier: HTTP 429
    /// above 50 requests per second per IP) counts in a sliding window, and a catch-up runs 16 block tasks at once.
    /// </summary>
    /// <remarks>
    /// The limit is per algod because only a public one needs it; our own nodes are configured with 0 (no limit), so a
    /// catch-up served by them is not slowed down. It is per process: every network deployed in the cluster shares one
    /// egress IP, so the default leaves room for a second pod catching up through the same public service.
    /// </remarks>
    public sealed class AlgodRateLimiter
    {
        private readonly long _intervalTicks;
        private readonly TimeProvider _time;
        private readonly object _lock = new();
        private long _nextSlotTicks;

        /// <param name="maxRequestsPerSecond">Zero or less: no limit.</param>
        public AlgodRateLimiter(double maxRequestsPerSecond, TimeProvider? timeProvider = null)
        {
            _intervalTicks = maxRequestsPerSecond > 0 ? (long)(TimeSpan.TicksPerSecond / maxRequestsPerSecond) : 0;
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Reserves the next request slot and returns how long the caller has to wait for it.</summary>
        public TimeSpan Reserve()
        {
            if (_intervalTicks == 0) return TimeSpan.Zero;
            var now = _time.GetUtcNow().UtcTicks;
            lock (_lock)
            {
                var slot = Math.Max(now, _nextSlotTicks);
                _nextSlotTicks = slot + _intervalTicks;
                return TimeSpan.FromTicks(slot - now);
            }
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            var wait = Reserve();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, cancellationToken);
        }
    }
}
