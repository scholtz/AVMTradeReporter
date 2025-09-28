using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    public class LiquidityQueryServiceTests
    {
        private LiquidityQueryService _liquidityQueryService;
        private ILogger<LiquidityQueryService> _logger;

        [SetUp]
        public void Setup()
        {
            // Create a mock service provider that returns null for ElasticsearchClient
            var serviceProvider = new LiquidityMockServiceProvider();
            _logger = new LiquidityServiceMockLogger<LiquidityQueryService>();
            _liquidityQueryService = new LiquidityQueryService(serviceProvider, _logger);
        }

        [Test]
        public async Task GetLiquidityAsync_WithoutElasticsearch_ReturnsEmptyList()
        {
            // Act
            var result = await _liquidityQueryService.GetLiquidityAsync();

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count(), Is.EqualTo(0));
        }

        [Test]
        public async Task GetLiquidityAsync_WithCancellation_ShouldNotThrow()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            Assert.DoesNotThrowAsync(async () =>
            {
                var result = await _liquidityQueryService.GetLiquidityAsync(cancellationToken: cts.Token);
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public async Task GetLiquidityAsync_WithParameters_DoesNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrowAsync(async () =>
            {
                var result = await _liquidityQueryService.GetLiquidityAsync(
                    assetIdA: 123,
                    assetIdB: 456,
                    txId: "test-tx-id",
                    offset: 10,
                    size: 50
                );
                Assert.That(result, Is.Not.Null);
            });
        }

        [Test]
        public void GetLiquidityAsync_ValidatesParameters()
        {
            // Test that the service accepts various parameter combinations without throwing
            Assert.DoesNotThrowAsync(async () => await _liquidityQueryService.GetLiquidityAsync(assetIdA: 123));
            Assert.DoesNotThrowAsync(async () => await _liquidityQueryService.GetLiquidityAsync(assetIdB: 456));
            Assert.DoesNotThrowAsync(async () => await _liquidityQueryService.GetLiquidityAsync(txId: "abc123"));
            Assert.DoesNotThrowAsync(async () => await _liquidityQueryService.GetLiquidityAsync(assetIdA: 123, assetIdB: 456));
        }

        [Test]
        public async Task GetLiquidityAsync_WithTxId_ReturnsEmptyWhenElasticsearchUnavailable()
        {
            // Arrange
            const string testTxId = "test-transaction-id";

            // Act
            var result = await _liquidityQueryService.GetLiquidityAsync(txId: testTxId);

            // Assert - Without Elasticsearch, should return empty list but not throw
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count(), Is.EqualTo(0));
        }

        [Test]
        public async Task GetLiquidityAsync_WithBothAssets_ReturnsEmptyWhenElasticsearchUnavailable()
        {
            // Arrange
            const ulong assetIdA = 123;
            const ulong assetIdB = 456;

            // Act
            var result = await _liquidityQueryService.GetLiquidityAsync(assetIdA: assetIdA, assetIdB: assetIdB);

            // Assert - Without Elasticsearch, should return empty list but not throw
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count(), Is.EqualTo(0));
        }

        [Test]
        public async Task GetLiquidityAsync_WithSingleAsset_ReturnsEmptyWhenElasticsearchUnavailable()
        {
            // Arrange
            const ulong assetId = 123;

            // Act
            var resultA = await _liquidityQueryService.GetLiquidityAsync(assetIdA: assetId);
            var resultB = await _liquidityQueryService.GetLiquidityAsync(assetIdB: assetId);

            // Assert - Without Elasticsearch, should return empty list but not throw
            Assert.That(resultA, Is.Not.Null);
            Assert.That(resultA.Count(), Is.EqualTo(0));
            Assert.That(resultB, Is.Not.Null);
            Assert.That(resultB.Count(), Is.EqualTo(0));
        }
    }

    // Mock classes for liquidity query service testing
    public class LiquidityMockServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            // Return null for ElasticsearchClient to simulate absence
            return null;
        }
    }

    public class LiquidityServiceMockLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Mock implementation - do nothing
        }
    }
}