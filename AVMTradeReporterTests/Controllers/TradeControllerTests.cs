using AVMTradeReporter.Controllers;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Controllers
{
    public class TradeControllerTests
    {
        private TradeController _controller;
        private MockTradeQueryService _mockTradeQueryService;
        private ILogger<TradeController> _logger;

        [SetUp]
        public void Setup()
        {
            _mockTradeQueryService = new MockTradeQueryService();
            _logger = new MockLogger<TradeController>();
            _controller = new TradeController(_mockTradeQueryService, _logger);

            // Setup HttpContext for cancellation token
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Test]
        public async Task GetTrades_NoParameters_ReturnsOkResult()
        {
            // Act
            var result = await _controller.GetTrades();

            // Assert
            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            var okResult = result.Result as OkObjectResult;
            Assert.That(okResult?.Value, Is.TypeOf<List<Trade>>());
        }

        [Test]
        public async Task GetTrades_WithAssetIdIn_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdIn = 123;

            // Act
            await _controller.GetTrades(assetIdIn: assetIdIn);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledAssetIdIn, Is.EqualTo(assetIdIn));
            Assert.That(_mockTradeQueryService.LastCalledAssetIdOut, Is.Null);
            Assert.That(_mockTradeQueryService.LastCalledTxId, Is.Null);
        }

        [Test]
        public async Task GetTrades_WithAssetIdOut_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdOut = 456;

            // Act
            await _controller.GetTrades(assetIdOut: assetIdOut);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledAssetIdIn, Is.Null);
            Assert.That(_mockTradeQueryService.LastCalledAssetIdOut, Is.EqualTo(assetIdOut));
            Assert.That(_mockTradeQueryService.LastCalledTxId, Is.Null);
        }

        [Test]
        public async Task GetTrades_WithTxId_CallsServiceCorrectly()
        {
            // Arrange
            const string txId = "test-tx-id";

            // Act
            await _controller.GetTrades(txId: txId);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledAssetIdIn, Is.Null);
            Assert.That(_mockTradeQueryService.LastCalledAssetIdOut, Is.Null);
            Assert.That(_mockTradeQueryService.LastCalledTxId, Is.EqualTo(txId));
        }

        [Test]
        public async Task GetTrades_WithPagination_CallsServiceCorrectly()
        {
            // Arrange
            const int offset = 20;
            const int size = 50;

            // Act
            await _controller.GetTrades(offset: offset, size: size);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledOffset, Is.EqualTo(offset));
            Assert.That(_mockTradeQueryService.LastCalledSize, Is.EqualTo(size));
        }

        [Test]
        public async Task GetTrades_SizeExceedsMax_ClampsToMaxValue()
        {
            // Arrange
            const int oversizedValue = 1000; // Max should be 500

            // Act
            await _controller.GetTrades(size: oversizedValue);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledSize, Is.EqualTo(500));
        }

        [Test]
        public async Task GetTrades_SizeBelowMin_ClampsToMinValue()
        {
            // Arrange
            const int undersizedValue = -10; // Min should be 1

            // Act
            await _controller.GetTrades(size: undersizedValue);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledSize, Is.EqualTo(1));
        }

        [Test]
        public async Task GetTrades_WithBothAssets_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdIn = 123;
            const ulong assetIdOut = 456;

            // Act
            await _controller.GetTrades(assetIdIn: assetIdIn, assetIdOut: assetIdOut);

            // Assert
            Assert.That(_mockTradeQueryService.LastCalledAssetIdIn, Is.EqualTo(assetIdIn));
            Assert.That(_mockTradeQueryService.LastCalledAssetIdOut, Is.EqualTo(assetIdOut));
        }
    }

    // Mock implementation for testing
    public class MockTradeQueryService : ITradeQueryService
    {
        public ulong? LastCalledAssetIdIn { get; private set; }
        public ulong? LastCalledAssetIdOut { get; private set; }
        public string? LastCalledTxId { get; private set; }
        public int LastCalledOffset { get; private set; }
        public int LastCalledSize { get; private set; }

        public Task<IEnumerable<Trade>> GetTradesAsync(
            ulong? assetIdIn = null,
            ulong? assetIdOut = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default)
        {
            LastCalledAssetIdIn = assetIdIn;
            LastCalledAssetIdOut = assetIdOut;
            LastCalledTxId = txId;
            LastCalledOffset = offset;
            LastCalledSize = size;

            // Return a sample trade for testing
            var trades = new List<Trade>
            {
                new Trade
                {
                    TxId = "sample-tx-id",
                    AssetIdIn = 123,
                    AssetIdOut = 456,
                    AssetAmountIn = 1000,
                    AssetAmountOut = 2000,
                    BlockId = 12345,
                    Timestamp = DateTimeOffset.Now
                }
            };

            return Task.FromResult<IEnumerable<Trade>>(trades);
        }
    }

    // Mock logger implementation
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