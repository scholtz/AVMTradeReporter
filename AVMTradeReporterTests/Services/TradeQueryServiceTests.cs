using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    public class TradeQueryServiceTests
    {
        private TradeQueryService _tradeQueryService;
        private ILogger<TradeQueryService> _logger;

        [SetUp]
        public void Setup()
        {
            // Create a mock service provider that returns null for ElasticsearchClient
            var serviceProvider = new MockServiceProvider();
            _logger = new MockLogger<TradeQueryService>();
            _tradeQueryService = new TradeQueryService(serviceProvider, _logger);
        }

        [Test]
        public async Task GetTradesAsync_WithoutElasticsearch_ReturnsEmptyList()
        {
            // Act
            var result = await _tradeQueryService.GetTradesAsync();

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count(), Is.EqualTo(0));
        }

        [Test]
        public async Task GetTradesAsync_WithCancellation_ShouldNotThrow()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            Assert.DoesNotThrowAsync(async () =>
            {
                var result = await _tradeQueryService.GetTradesAsync(cancellationToken: cts.Token);
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public async Task GetTradesAsync_WithParameters_DoesNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrowAsync(async () =>
            {
                var result = await _tradeQueryService.GetTradesAsync(
                    assetIdIn: 123,
                    assetIdOut: 456,
                    txId: "test-tx-id",
                    offset: 10,
                    size: 50
                );
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public void GetTradesAsync_ValidatesParameters()
        {
            // Test that the service accepts various parameter combinations without throwing
            Assert.DoesNotThrowAsync(async () => await _tradeQueryService.GetTradesAsync(assetIdIn: 123));
            Assert.DoesNotThrowAsync(async () => await _tradeQueryService.GetTradesAsync(assetIdOut: 456));
            Assert.DoesNotThrowAsync(async () => await _tradeQueryService.GetTradesAsync(txId: "abc123"));
            Assert.DoesNotThrowAsync(async () => await _tradeQueryService.GetTradesAsync(assetIdIn: 123, assetIdOut: 456));
        }

        [Test]
        public async Task GetTradesAsync_WithTxId_ReturnsEmptyWhenElasticsearchUnavailable()
        {
            // Arrange
            const string testTxId = "test-transaction-id";

            // Act
            var result = await _tradeQueryService.GetTradesAsync(txId: testTxId);

            // Assert - Without Elasticsearch, should return empty list but not throw
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count(), Is.EqualTo(0));
        }
    }

    // Mock classes for testing
    public class MockServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            // Return null for ElasticsearchClient to simulate absence
            return null;
        }
    }

    public class MockLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Mock implementation - do nothing
        }
    }
}