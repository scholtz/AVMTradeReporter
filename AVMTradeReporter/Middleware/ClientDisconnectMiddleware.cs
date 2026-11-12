namespace AVMTradeReporter.Middleware
{
    /// <summary>
    /// Ends a request that was aborted mid-flight (the caller closed the tab, navigated off the chart, timed out on its side)
    /// quietly instead of letting the cancellation reach Kestrel, which logs it as "An unhandled exception was thrown by the
    /// application" at ERROR. The abort cancels HttpContext.RequestAborted, and whatever the action was awaiting fails with it -
    /// but not always as an OperationCanceledException: the Elasticsearch client (built with ThrowExceptions() in Program.cs)
    /// wraps the cancellation in an Elastic.Transport.TransportException ("The operation was canceled. Call: Status code unknown
    /// from: POST /ohlc/_search"). See <see cref="IsClientAbort(Exception, HttpContext?)"/> for what counts.
    /// Covers every endpoint behind it on purpose (controllers, health checks, the SignalR hub request): a cancellation on an
    /// aborted connection is no more interesting there. Controllers with their own blanket catch exclude the same case with
    /// <c>when (!ClientDisconnectMiddleware.IsClientAbort(ex, HttpContext))</c> so it lands here instead of in their LogError + 500.
    /// </summary>
    public class ClientDisconnectMiddleware
    {
        /// <summary>nginx's non-standard "client closed request" status - the same one the controllers' own catch blocks answer with.</summary>
        public const int ClientClosedRequestStatusCode = 499;

        private readonly RequestDelegate _next;
        private readonly ILogger<ClientDisconnectMiddleware> _logger;

        public ClientDisconnectMiddleware(RequestDelegate next, ILogger<ClientDisconnectMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// True when <paramref name="ex"/> is the request's own abort surfacing: the request token is cancelled AND the exception
        /// (or an inner one) is an OperationCanceledException. Both are needed - a cancellation while the caller is still connected
        /// is a server-side timeout (a real failure), and a non-cancellation fault (NullReferenceException, an Elasticsearch error
        /// answer) that merely coincides with the caller giving up on a slow backend must stay an ERROR.
        /// RequestAborted is also cancelled when the server itself drops the connection (shutdown), which is equally not a fault of
        /// the request. A null context (a controller built without one in a unit test) is never an abort.
        /// </summary>
        public static bool IsClientAbort(Exception? ex, HttpContext? context)
            => context != null && IsClientAbort(ex, context.RequestAborted);

        /// <inheritdoc cref="IsClientAbort(Exception, HttpContext?)"/>
        public static bool IsClientAbort(Exception? ex, CancellationToken requestAborted)
        {
            if (!requestAborted.IsCancellationRequested) return false;
            for (var depth = 0; ex != null && depth < 16; ex = ex.InnerException, depth++)
            {
                if (ex is OperationCanceledException) return true;
            }
            return false;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex) when (IsClientAbort(ex, context))
            {
                _logger.LogDebug(ex, "Request aborted before {Method} {Path} completed", context.Request.Method, context.Request.Path);
                // Nobody reads it, but the status keeps access logs / metrics from counting the request as a 200. Clear() first so
                // headers the failed action already set (Content-Length, Cache-Control) do not ride along on an empty 499.
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = ClientClosedRequestStatusCode;
                }
            }
        }
    }
}
