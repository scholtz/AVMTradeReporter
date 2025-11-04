using AVMTradeReporter.Controllers;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Controllers
{
    public class LiquidityControllerTests
    {
        private LiquidityController _controller;
        private MockLiquidityQueryService _mockLiquidityQueryService;
        private ILogger<LiquidityController> _logger;

        [SetUp]
        public void Setup()
        {
            _mockLiquidityQueryService = new MockLiquidityQueryService();
            _logger = new LiquidityMockLogger<LiquidityController>();
            _controller = new LiquidityController(_mockLiquidityQueryService, _logger);

            // Setup HttpContext for cancellation token
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Test]
        public async Task GetLiquidity_NoParameters_ReturnsOkResult()
        {
            // Act
            var result = await _controller.GetLiquidity();

            // Assert
            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            var okResult = result.Result as OkObjectResult;
            Assert.That(okResult?.Value, Is.TypeOf<List<Liquidity>>());
        }

        [Test]
        public async Task GetLiquidity_WithAssetIdA_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdA = 123;

            // Act
            await _controller.GetLiquidity(assetIdA: assetIdA);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdA, Is.EqualTo(assetIdA));
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdB, Is.Null);
            Assert.That(_mockLiquidityQueryService.LastCalledTxId, Is.Null);
        }

        [Test]
        public async Task GetLiquidity_WithAssetIdB_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdB = 456;

            // Act
            await _controller.GetLiquidity(assetIdB: assetIdB);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdA, Is.Null);
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdB, Is.EqualTo(assetIdB));
            Assert.That(_mockLiquidityQueryService.LastCalledTxId, Is.Null);
        }

        [Test]
        public async Task GetLiquidity_WithTxId_CallsServiceCorrectly()
        {
            // Arrange
            const string txId = "test-tx-id";

            // Act
            await _controller.GetLiquidity(txId: txId);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdA, Is.Null);
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdB, Is.Null);
            Assert.That(_mockLiquidityQueryService.LastCalledTxId, Is.EqualTo(txId));
        }

        [Test]
        public async Task GetLiquidity_WithPagination_CallsServiceCorrectly()
        {
            // Arrange
            const int offset = 20;
            const int size = 50;

            // Act
            await _controller.GetLiquidity(offset: offset, size: size);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledOffset, Is.EqualTo(offset));
            Assert.That(_mockLiquidityQueryService.LastCalledSize, Is.EqualTo(size));
        }

        [Test]
        public async Task GetLiquidity_SizeExceedsMax_ClampsToMaxValue()
        {
            // Arrange
            const int oversizedValue = 1000; // Max should be 500

            // Act
            await _controller.GetLiquidity(size: oversizedValue);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledSize, Is.EqualTo(500));
        }

        [Test]
        public async Task GetLiquidity_SizeBelowMin_ClampsToMinValue()
        {
            // Arrange
            const int undersizedValue = -10; // Min should be 1

            // Act
            await _controller.GetLiquidity(size: undersizedValue);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledSize, Is.EqualTo(1));
        }

        [Test]
        public async Task GetLiquidity_WithBothAssets_CallsServiceCorrectly()
        {
            // Arrange
            const ulong assetIdA = 123;
            const ulong assetIdB = 456;

            // Act
            await _controller.GetLiquidity(assetIdA: assetIdA, assetIdB: assetIdB);

            // Assert
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdA, Is.EqualTo(assetIdA));
            Assert.That(_mockLiquidityQueryService.LastCalledAssetIdB, Is.EqualTo(assetIdB));
        }
    }

    // Mock implementation for testing
    public class MockLiquidityQueryService : ILiquidityQueryService
    {
        public ulong? LastCalledAssetIdA { get; private set; }
        public ulong? LastCalledAssetIdB { get; private set; }
        public string? LastCalledTxId { get; private set; }
        public int LastCalledOffset { get; private set; }
        public int LastCalledSize { get; private set; }

        public Task<IEnumerable<Liquidity>> GetLiquidityAsync(
            ulong? assetIdA = null,
            ulong? assetIdB = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default)
        {
            LastCalledAssetIdA = assetIdA;
            LastCalledAssetIdB = assetIdB;
            LastCalledTxId = txId;
            LastCalledOffset = offset;
            LastCalledSize = size;

            // Return a sample liquidity for testing
            var liquidityUpdates = new List<Liquidity>
            {
                new Liquidity
                {
                    TxId = "sample-liquidity-tx-id",
                    AssetIdA = 123,
                    AssetIdB = 456,
                    AssetAmountA = 1000,
                    AssetAmountB = 2000,
                    BlockId = 12345,
                    Timestamp = DateTimeOffset.Now,
                    Direction = LiqudityDirection.DepositLiquidity,
                    Protocol = DEXProtocol.Pact,
                    LiquidityProvider = "LIQUIDITYPROVIDERADDRESS",
                    PoolAddress = "POOLADDRESS",
                    PoolAppId = 1001
                }
            };

            return Task.FromResult<IEnumerable<Liquidity>>(liquidityUpdates);
        }
    }

    // Mock logger implementation for liquidity controller tests  
    public class LiquidityMockLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Mock implementation - do nothing
        }
    }
}