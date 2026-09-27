using AVMTradeReporter.Controllers;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="StatsController"/>, focused on the optional
    /// <c>to</c> query parameter that enables arbitrary (e.g. hourly) DEX stats windows.
    /// </summary>
    public class StatsControllerTests
    {
        private StatsController _controller = null!;
        private MockStatsService _mockStatsService = null!;

        [SetUp]
        public void Setup()
        {
            _mockStatsService = new MockStatsService();
            _controller = new StatsController(_mockStatsService, new StatsMockLogger<StatsController>());
        }

        [Test]
        public async Task GetDexStats_UnknownProtocol_ReturnsBadRequest()
        {
            var result = await _controller.GetDexStats("NotARealDex", DateTimeOffset.UtcNow, null, CancellationToken.None);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetDexStats_ToBeforeTimestamp_ReturnsBadRequest()
        {
            var timestamp = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
            var to = timestamp.AddHours(-1);

            var result = await _controller.GetDexStats("Biatec", timestamp, to, CancellationToken.None);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetDexStats_ToEqualsTimestamp_ReturnsBadRequest()
        {
            var timestamp = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

            var result = await _controller.GetDexStats("Biatec", timestamp, timestamp, CancellationToken.None);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetDexStats_NoTo_ForwardsNullToService()
        {
            var timestamp = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

            var result = await _controller.GetDexStats("Biatec", timestamp, null, CancellationToken.None);

            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(_mockStatsService.LastTo, Is.Null);
        }

        [Test]
        public async Task GetDexStats_WithValidTo_ForwardsWindowToService()
        {
            var timestamp = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
            var to = timestamp.AddHours(1);

            var result = await _controller.GetDexStats("Biatec", timestamp, to, CancellationToken.None);

            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(_mockStatsService.LastFrom, Is.EqualTo(timestamp));
            Assert.That(_mockStatsService.LastTo, Is.EqualTo(to));
        }
    }

    /// <summary>In-memory stub for <see cref="IStatsService"/> used in controller tests.</summary>
    internal sealed class MockStatsService : IStatsService
    {
        public DateTimeOffset? LastFrom { get; private set; }
        public DateTimeOffset? LastTo { get; private set; }

        public Task<DexStatsResponse> GetDexStatsAsync(
            DEXProtocol protocol,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            CancellationToken cancellationToken = default)
        {
            LastFrom = from;
            LastTo = to;

            return Task.FromResult(new DexStatsResponse
            {
                Protocol = protocol.ToString(),
                From = from,
                To = to ?? from.AddDays(1)
            });
        }
    }

    /// <summary>Mock logger implementation for stats controller tests.</summary>
    internal sealed class StatsMockLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Mock implementation - do nothing
        }
    }
}
