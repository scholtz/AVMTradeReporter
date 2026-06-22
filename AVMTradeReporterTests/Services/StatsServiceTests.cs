using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Unit tests for <see cref="StatsService"/> using a mock repository so that no
    /// Elasticsearch connection is required.
    /// </summary>
    [TestFixture]
    public class StatsServiceTests
    {
        private StatsService _statsService = null!;
        private MockStatsRepository _mockRepository = null!;

        [SetUp]
        public void Setup()
        {
            _mockRepository = new MockStatsRepository();
            // MockLogger<T> is defined in TradeQueryServiceTests.cs within this namespace.
            _statsService = new StatsService(_mockRepository, new MockLogger<StatsService>());
        }

        [Test]
        public async Task GetDexStatsAsync_ReturnsCorrectProtocolName()
        {
            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, DateTimeOffset.UtcNow);

            Assert.That(result.Protocol, Is.EqualTo("Biatec"));
        }

        [Test]
        public async Task GetDexStatsAsync_TimeWindowIsExactlyOneDayForward()
        {
            var from = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Pact, from);

            Assert.That(result.From, Is.EqualTo(from));
            Assert.That(result.To, Is.EqualTo(from.AddDays(1)));
        }

        [Test]
        public async Task GetDexStatsAsync_MapsRepositoryAggregationsToResponse()
        {
            _mockRepository.VolumeUSD = 2071.755072593689;
            _mockRepository.FeesUSD = 0.34298487441265024;
            _mockRepository.FeesUSDProvider = 0.2743879053450655;
            _mockRepository.FeesUSDProtocol = 0.06859697633626638;

            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, DateTimeOffset.UtcNow);

            Assert.That((double)result.VolumeUSD, Is.EqualTo(2071.755072593689).Within(0.001));
            Assert.That((double)result.FeesUSD, Is.EqualTo(0.34298487441265024).Within(0.0001));
            Assert.That((double)result.FeesLPUSD, Is.EqualTo(0.2743879053450655).Within(0.0001));
            Assert.That((double)result.FeesProtocolUSD, Is.EqualTo(0.06859697633626638).Within(0.0001));
        }

        [Test]
        public async Task GetDexStatsAsync_WithNoTrades_ReturnsZeroStats()
        {
            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Tiny, DateTimeOffset.UtcNow);

            Assert.That(result.VolumeUSD, Is.EqualTo(0m));
            Assert.That(result.FeesUSD, Is.EqualTo(0m));
            Assert.That(result.FeesLPUSD, Is.EqualTo(0m));
            Assert.That(result.FeesProtocolUSD, Is.EqualTo(0m));
        }

        [Test]
        [TestCase(DEXProtocol.Biatec, "Biatec")]
        [TestCase(DEXProtocol.Pact, "Pact")]
        [TestCase(DEXProtocol.Tiny, "Tiny")]
        public async Task GetDexStatsAsync_ForwardsProtocolStringToRepository(DEXProtocol protocol, string expected)
        {
            await _statsService.GetDexStatsAsync(protocol, DateTimeOffset.UtcNow);

            Assert.That(_mockRepository.LastProtocol, Is.EqualTo(expected));
        }

        [Test]
        public async Task GetDexStatsAsync_ForwardsCorrectTimeWindowToRepository()
        {
            var from = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

            await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, from);

            Assert.That(_mockRepository.LastFrom, Is.EqualTo(from));
            Assert.That(_mockRepository.LastTo, Is.EqualTo(from.AddDays(1)));
        }

        [Test]
        public async Task GetDexStatsAsync_WhenRepositoryThrows_ReturnsZeroedResponseWithCorrectMetadata()
        {
            _mockRepository.ThrowOnQuery = true;
            var from = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, from);

            Assert.That(result.VolumeUSD, Is.EqualTo(0m));
            Assert.That(result.FeesUSD, Is.EqualTo(0m));
            Assert.That(result.Protocol, Is.EqualTo("Biatec"));
            Assert.That(result.From, Is.EqualTo(from));
            Assert.That(result.To, Is.EqualTo(from.AddDays(1)));
        }

        [Test]
        public async Task GetDexStatsAsync_DoesNotThrow_WithCancelledToken()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The mock repository returns synchronously, so cancellation is swallowed gracefully.
            Assert.DoesNotThrowAsync(async () =>
            {
                var result = await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, DateTimeOffset.UtcNow, cts.Token);
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public async Task GetDexStatsAsync_ResultIsNotNull()
        {
            var result = await _statsService.GetDexStatsAsync(DEXProtocol.Biatec, DateTimeOffset.UtcNow);

            Assert.That(result, Is.Not.Null);
        }
    }

    /// <summary>
    /// In-memory stub for <see cref="IStatsRepository"/> used in unit tests.
    /// </summary>
    internal sealed class MockStatsRepository : IStatsRepository
    {
        /// <summary>Value returned as VolumeUSD by <see cref="GetDexAggregationsAsync"/>.</summary>
        public double VolumeUSD { get; set; }

        /// <summary>Value returned as FeesUSD.</summary>
        public double FeesUSD { get; set; }

        /// <summary>Value returned as FeesUSDProvider.</summary>
        public double FeesUSDProvider { get; set; }

        /// <summary>Value returned as FeesUSDProtocol.</summary>
        public double FeesUSDProtocol { get; set; }

        /// <summary>When <see langword="true"/>, the method throws to simulate a repository failure.</summary>
        public bool ThrowOnQuery { get; set; }

        /// <summary>Protocol string received in the most recent call.</summary>
        public string? LastProtocol { get; private set; }

        /// <summary>From timestamp received in the most recent call.</summary>
        public DateTimeOffset? LastFrom { get; private set; }

        /// <summary>To timestamp received in the most recent call.</summary>
        public DateTimeOffset? LastTo { get; private set; }

        /// <inheritdoc />
        public Task<(double VolumeUSD, double FeesUSD, double FeesUSDProvider, double FeesUSDProtocol)> GetDexAggregationsAsync(
            string protocol,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            LastProtocol = protocol;
            LastFrom = from;
            LastTo = to;

            if (ThrowOnQuery)
                throw new InvalidOperationException("Simulated repository failure.");

            return Task.FromResult((VolumeUSD, FeesUSD, FeesUSDProvider, FeesUSDProtocol));
        }
    }
}
