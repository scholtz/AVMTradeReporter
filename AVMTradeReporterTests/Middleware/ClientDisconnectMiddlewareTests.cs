using AVMTradeReporter.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Middleware
{
    public class ClientDisconnectMiddlewareTests
    {
        private sealed class RecordingLogger : ILogger<ClientDisconnectMiddleware>
        {
            public List<LogLevel> Levels { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Levels.Add(logLevel);
        }

        /// <summary>The shape the Elasticsearch client throws when the request token is cancelled mid-search: not an OperationCanceledException.</summary>
        private static Exception ElasticCancellation() => new Elastic.Transport.TransportException(
            Elastic.Transport.PipelineFailure.Unexpected,
            "The operation was canceled. Call: Status code unknown from: POST /ohlc/_search",
            new TaskCanceledException("The operation was canceled."));

        private static (DefaultHttpContext context, CancellationTokenSource aborted) NewContext()
        {
            var aborted = new CancellationTokenSource();
            var context = new DefaultHttpContext { RequestAborted = aborted.Token };
            return (context, aborted);
        }

        [Test]
        public async Task ClientAborted_ElasticTransportException_IsSwallowedAs499()
        {
            var (context, aborted) = NewContext();
            var logger = new RecordingLogger();
            var middleware = new ClientDisconnectMiddleware(_ =>
            {
                aborted.Cancel();
                throw ElasticCancellation();
            }, logger);

            await middleware.InvokeAsync(context);

            Assert.That(context.Response.StatusCode, Is.EqualTo(ClientDisconnectMiddleware.ClientClosedRequestStatusCode));
            Assert.That(logger.Levels, Is.EqualTo(new[] { LogLevel.Debug }));
        }

        [Test]
        public async Task ClientAborted_OperationCanceledException_IsSwallowedAs499()
        {
            var (context, aborted) = NewContext();
            var middleware = new ClientDisconnectMiddleware(_ =>
            {
                aborted.Cancel();
                throw new OperationCanceledException(aborted.Token);
            }, new RecordingLogger());

            await middleware.InvokeAsync(context);

            Assert.That(context.Response.StatusCode, Is.EqualTo(ClientDisconnectMiddleware.ClientClosedRequestStatusCode));
        }

        [Test]
        public async Task ClientStillConnected_ExceptionPropagates()
        {
            // An Elasticsearch-side timeout / cancellation with the caller still waiting is a real failure and must stay loud.
            var (context, _) = NewContext();
            var logger = new RecordingLogger();
            var middleware = new ClientDisconnectMiddleware(_ => throw ElasticCancellation(), logger);

            await Assert.ThrowsAsync<Elastic.Transport.TransportException>(() => middleware.InvokeAsync(context));
            Assert.That(logger.Levels, Is.Empty);
        }

        [Test]
        public async Task ClientAborted_NonCancellationFault_Propagates()
        {
            // A real bug that merely coincides with the caller giving up on a slow request must stay an ERROR.
            var (context, aborted) = NewContext();
            var logger = new RecordingLogger();
            var middleware = new ClientDisconnectMiddleware(_ =>
            {
                aborted.Cancel();
                throw new InvalidOperationException("boom");
            }, logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
            Assert.That(logger.Levels, Is.Empty);
        }

        [Test]
        public async Task ClientAborted_HeadersSetByFailedAction_AreCleared()
        {
            var (context, aborted) = NewContext();
            var middleware = new ClientDisconnectMiddleware(ctx =>
            {
                ctx.Response.Headers["Cache-Control"] = "public,max-age=300";
                ctx.Response.ContentLength = 1234;
                aborted.Cancel();
                throw ElasticCancellation();
            }, new RecordingLogger());

            await middleware.InvokeAsync(context);

            Assert.That(context.Response.StatusCode, Is.EqualTo(ClientDisconnectMiddleware.ClientClosedRequestStatusCode));
            Assert.That(context.Response.Headers.ContainsKey("Cache-Control"), Is.False);
            Assert.That(context.Response.ContentLength, Is.Null);
        }

        [Test]
        public void IsClientAbort_NullContext_IsFalse()
        {
            Assert.That(ClientDisconnectMiddleware.IsClientAbort(new OperationCanceledException(), (HttpContext?)null), Is.False);
        }

        [Test]
        public async Task Controller_BlanketCatch_LetsElasticWrappedAbortThrough()
        {
            // AssetController-style "catch (Exception) -> LogError + 500" must not claim the abort: it has to reach the middleware.
            using var aborted = new CancellationTokenSource();
            var topAssets = new Moq.Mock<AVMTradeReporter.Services.ITopAssetsService>();
            topAssets.Setup(s => s.GetTopAssetsAsync(Moq.It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(_ => { aborted.Cancel(); throw ElasticCancellation(); });
            var controller = new AVMTradeReporter.Controllers.TopAssetsController(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AVMTradeReporter.Controllers.TopAssetsController>.Instance, topAssets.Object)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
            };

            await Assert.ThrowsAsync<Elastic.Transport.TransportException>(() => controller.GetTopAssets());
        }

        [Test]
        public async Task NoException_ResponseUntouched()
        {
            var (context, _) = NewContext();
            var middleware = new ClientDisconnectMiddleware(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            }, new RecordingLogger());

            await middleware.InvokeAsync(context);

            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        }
    }
}
