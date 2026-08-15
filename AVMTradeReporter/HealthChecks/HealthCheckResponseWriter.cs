using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Renders the standard ASP.NET Core HealthReport as the JSON shape external monitors expect:
    /// an overall Healthy/Degraded/Unhealthy status plus a per-check breakdown.
    /// </summary>
    public static class HealthCheckResponseWriter
    {
        public static Task WriteResponse(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = e.Value.Duration.TotalMilliseconds,
                }),
            };
            return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }
    }
}
