using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Block requests to one algod never exceed its rate limit, however many block tasks ask at once. See CLAUDE.md,
    /// "Sync speed", rule 5.
    /// </summary>
    [TestFixture]
    public class AlgodRateLimiterTests
    {
        private sealed class FakeTime : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-12T20:00:00Z");
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        [Test]
        public void NoLimit_NeverWaits()
        {
            var limiter = new AlgodRateLimiter(0, new FakeTime());
            for (var i = 0; i < 1000; i++) Assert.That(limiter.Reserve(), Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void SixteenTasksAtOnce_AreSpreadOverTheirSlots_NotSentAsABurst()
        {
            var limiter = new AlgodRateLimiter(25, new FakeTime());
            var waits = Enumerable.Range(0, 16).Select(_ => limiter.Reserve()).ToArray();

            Assert.That(waits[0], Is.EqualTo(TimeSpan.Zero));
            for (var i = 1; i < waits.Length; i++) Assert.That(waits[i], Is.EqualTo(TimeSpan.FromMilliseconds(40 * i)), "one slot of 1/25 s per request");
        }

        [Test]
        public void NoSecond_HoldsMoreRequestsThanTheLimit_WhateverThePattern()
        {
            // bursts of 16 (a catch-up's block tasks), pauses of an Elasticsearch refresh wait, retries: a sliding second
            // never holds more than the limit - a fixed window would let two bursts through around its boundary
            const int limit = 25;
            var time = new FakeTime();
            var start = time.GetUtcNow();
            var limiter = new AlgodRateLimiter(limit, time);
            var sentAt = new List<TimeSpan>();
            var random = new Random(7);

            for (var burst = 0; burst < 200; burst++)
            {
                for (var i = 0; i < 16; i++) sentAt.Add(time.GetUtcNow() - start + limiter.Reserve());
                time.Advance(TimeSpan.FromMilliseconds(random.Next(0, 1200)));
            }

            sentAt.Sort();
            for (var i = 0; i + limit < sentAt.Count; i++)
            {
                Assert.That(sentAt[i + limit] - sentAt[i], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1)), $"request {i} and the {limit}th after it are at least a second apart");
            }
        }

        [Test]
        public void IdleTime_IsNotSavedUpForABurst()
        {
            var time = new FakeTime();
            var limiter = new AlgodRateLimiter(25, time);
            limiter.Reserve();
            time.Advance(TimeSpan.FromMinutes(10)); // at the chain tip: one request every few seconds

            Assert.That(limiter.Reserve(), Is.EqualTo(TimeSpan.Zero));
            Assert.That(limiter.Reserve(), Is.EqualTo(TimeSpan.FromMilliseconds(40)), "the second request of a burst waits its slot");
        }
    }
}
