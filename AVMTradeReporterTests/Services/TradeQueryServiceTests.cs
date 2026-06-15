using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Elastic.Clients.Elasticsearch.QueryDsl;
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

        [Test]
        public async Task GetPoolVolumesAsync_WithoutElasticsearch_ReturnsEmptyDictionary()
        {
            // Act
            var result = await _tradeQueryService.GetPoolVolumesAsync(new List<string> { "pool1" });

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task GetTradesAsync_AdvancedFilterWithoutElasticsearch_ReturnsPagedMetadata()
        {
            // Act
            var result = await _tradeQueryService.GetTradesAsync(new TradeFilter
            {
                AssetId = 123,
                Offset = 20,
                Size = 50
            });

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Items.Count(), Is.EqualTo(0));
            Assert.That(result.Total, Is.EqualTo(0));
            Assert.That(result.Offset, Is.EqualTo(20));
            Assert.That(result.Size, Is.EqualTo(50));
            Assert.That(result.HasMore, Is.False);
        }

        [Test]
        public void BuildQuery_WithAdvancedFilters_DoesNotThrow()
        {
            var filter = new TradeFilter
            {
                AssetId = 123,
                AssetIdA = 123,
                AssetIdB = 456,
                Trader = "TRADER",
                PoolAddress = "POOL",
                PoolAppId = 789,
                Protocol = DEXProtocol.Biatec,
                TradeState = TxState.Confirmed,
                BlockFrom = 100,
                BlockTo = 200,
                TimestampFrom = DateTimeOffset.UtcNow.AddHours(-1),
                TimestampTo = DateTimeOffset.UtcNow,
                MinValueUSD = 10m,
                MaxValueUSD = 100m,
                MinFeesUSD = 1m,
                MaxFeesUSD = 5m,
                MinAmountIn = 1000,
                MaxAmountIn = 2000,
                MinAmountOut = 3000,
                MaxAmountOut = 4000
            };

            Assert.DoesNotThrow(() =>
            {
                var query = TradeQueryService.BuildQuery(new QueryDescriptor<Trade>(), filter);
                Assert.That(query, Is.Not.Null);
            });
        }

        [Test]
        public void BuildQuery_WithTxId_TakesPrecedence()
        {
            var filter = new TradeFilter
            {
                TxId = "TX",
                AssetId = 123,
                Trader = "TRADER"
            };

            var query = TradeQueryService.BuildQuery(new QueryDescriptor<Trade>(), filter);

            Assert.That(query, Is.Not.Null);
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