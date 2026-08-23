using AVMTradeReporter.Repository;
using NUnit.Framework;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Bucket-generation and tick mechanics of <see cref="TvlOhlcRepository"/> — the TVL counterpart
    /// of <see cref="OHLCRepositoryTests"/>. Simpler than price OHLC: a single asset id's USD value,
    /// no pair, no volume, so every interval bucket is generated unconditionally (no trust/anchor
    /// gating to test).
    /// </summary>
    public class TvlOhlcRepositoryTests
    {
        [Test]
        public void GetIntervalBuckets_GeneratesOneBucketPerInterval()
        {
            var ts = DateTimeOffset.Parse("2024-01-02T03:04:05Z");
            var buckets = TvlOhlcRepository.GetIntervalBuckets(assetId: 12345UL, tvlUsd: 987.65m, ts);

            Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(buckets.Select(b => b.Interval), Is.EquivalentTo(OHLCRepository.Intervals.Select(i => i.code)));
            foreach (var b in buckets)
            {
                Assert.That(b.AssetId, Is.EqualTo(12345UL));
                Assert.That(b.TvlUsd, Is.EqualTo(987.65m));
                Assert.That(b.DocId, Is.EqualTo($"12345-{b.Interval}-{b.BucketStart:yyyyMMddHHmmss}"));
            }
        }

        [Test]
        public void GetIntervalBuckets_BucketStartsAlignWithPriceOhlcBucketing()
        {
            // Same timestamp, same interval spans: TVL and price candles must land on identical
            // bucket boundaries so a future combined chart (price + liquidity) lines up.
            var ts = DateTimeOffset.Parse("2024-06-15T13:47:22Z");
            var tvlBuckets = TvlOhlcRepository.GetIntervalBuckets(1UL, 100m, ts)
                .ToDictionary(b => b.Interval, b => b.BucketStart);

            foreach (var (code, span) in OHLCRepository.Intervals)
            {
                var expected = OHLCRepository.GetBucketStart(ts, span);
                Assert.That(tvlBuckets[code], Is.EqualTo(expected), $"interval {code} bucket start mismatch");
            }
        }

        [Test]
        public void GetTick_BuildsOneTickFromTvlObservation()
        {
            var ts = DateTimeOffset.Parse("2024-01-02T03:04:05Z");
            var tick = TvlOhlcRepository.GetTick(assetId: 42UL, tvlUsd: 1234.5m, ts);

            Assert.That(tick.AssetId, Is.EqualTo(42UL));
            Assert.That(tick.Tvl, Is.EqualTo(1234.5m));
            Assert.That(tick.Timestamp, Is.EqualTo(ts.ToUnixTimeSeconds()));
        }

        [Test]
        public void Constructor_WithNullElasticClient_DoesNotThrow()
        {
            // Mirrors OHLCRepositoryTests' `new OHLCRepository(null, null)` pattern: CreateTemplateAsync
            // no-ops when the client is null, so construction (and thus DI resolution when ES is
            // unavailable) must not throw.
            Assert.DoesNotThrow(() => new TvlOhlcRepository(null!, null!));
        }

        [Test]
        public async Task UpdateFromTvlChangeAsync_WithNullElasticClientAndNoHub_DoesNotThrow()
        {
            var repo = new TvlOhlcRepository(null!, null!);
            await repo.UpdateFromTvlChangeAsync(1UL, 100m, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        [Test]
        public async Task GetCandlesAsync_WithNullElasticClient_ReturnsEmptyCandles()
        {
            var repo = new TvlOhlcRepository(null!, null!);
            var candles = await repo.GetCandlesAsync(1UL, DateTimeOffset.UtcNow, 168, CancellationToken.None);

            Assert.That(candles.T, Is.Empty);
            Assert.That(candles.O, Is.Empty);
        }

        [Test]
        public async Task GetTvlAtOrBeforeAsync_WithNullElasticClient_ReturnsEmptyDictionary()
        {
            var repo = new TvlOhlcRepository(null!, null!);
            var result = await repo.GetTvlAtOrBeforeAsync(new ulong[] { 1, 2, 3 }, DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task GetTvlAtOrBeforeAsync_WithEmptyAssetIds_ReturnsEmptyDictionary()
        {
            var repo = new TvlOhlcRepository(null!, null!);
            var result = await repo.GetTvlAtOrBeforeAsync(Array.Empty<ulong>(), DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.That(result, Is.Empty);
        }
    }
}
