namespace AVMTradeReporter.Middleware
{
    /// <summary>
    /// Ends a request whose client went away (closed the tab, navigated off the chart, timed out on its side) quietly instead of
    /// letting the failure reach Kestrel, which logs it as "An unhandled exception was thrown by the application" at ERROR.
    /// The abort cancels HttpContext.RequestAborted, and whatever the action was awaiting fails with it - but not always as an
    /// OperationCanceledException: the Elasticsearch client (built with ThrowExceptions() in Program.cs) wraps the cancellation in
    /// an Elastic.Transport.TransportException ("The operation was canceled. Call: Status code unknown from: POST /ohlc/_search"),
    /// so the exception type says nothing. The request's own token does: when it is cancelled nobody is left to read a response,
    /// so whatever was thrown after that is noise. A failure on a request that is still connected (an Elasticsearch timeout, a
    /// real bug) is never swallowed.
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

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Client disconnected before {Method} {Path} completed", context.Request.Method, context.Request.Path);
                // Nobody reads it, but the status keeps access logs / metrics from counting the request as a 200.
                if (!context.Response.HasStarted) context.Response.StatusCode = ClientClosedRequestStatusCode;
            }
        }
    }
}
