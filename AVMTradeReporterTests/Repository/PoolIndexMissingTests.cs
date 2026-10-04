using System.Net;
using System.Text;
using PoolModel = AVMTradeReporter.Models.Data.Pool;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Voi has no pool, so its Elasticsearch has no 'pools' index. The production client is built with ThrowExceptions(), so the
    /// missing index is thrown - these tests drive the REAL client (same settings as Program.cs) against a local server that
    /// answers like Elasticsearch does, and prove the thrown exception is recognised as "answered: empty", not "load failed".
    /// </summary>
    public class PoolIndexMissingTests
    {
        private const string IndexNotFoundBody =
            "{\"error\":{\"root_cause\":[{\"type\":\"index_not_found_exception\",\"reason\":\"no such index [pools]\",\"resource.type\":\"index_or_alias\",\"resource.id\":\"pools\",\"index_uuid\":\"_na_\",\"index\":\"pools\"}],\"type\":\"index_not_found_exception\",\"reason\":\"no such index [pools]\",\"resource.type\":\"index_or_alias\",\"resource.id\":\"pools\",\"index_uuid\":\"_na_\",\"index\":\"pools\"},\"status\":404}";

        private static async Task<Exception?> SearchPoolsAgainst(HttpStatusCode status, string body)
        {
            var port = new Random().Next(20000, 40000);
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { return; }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.Headers.Add("X-Elastic-Product", "Elasticsearch");
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });

            // the production settings of Program.cs that decide how an error is reported
            var settings = new ElasticsearchClientSettings(new Uri($"http://localhost:{port}"))
                .ThrowExceptions()
                .EnableDebugMode()
                .PrettyJson()
                .RequestTimeout(TimeSpan.FromSeconds(10));
            var client = new ElasticsearchClient(settings);
            try
            {
                await client.SearchAsync<PoolModel>(s => s.Indices("pools").Size(10000));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                listener.Stop();
                await serve;
            }
        }

        [Test]
        public async Task TheThrownMissingIndexAnswer_IsRecognised()
        {
            var ex = await SearchPoolsAgainst(HttpStatusCode.NotFound, IndexNotFoundBody);
            Assert.That(ex, Is.Not.Null, "ThrowExceptions() must throw for a 404");
            Assert.That(PoolRepository.IsIndexNotFound(ex), Is.True, $"recognised: {ex}");
        }

        [Test]
        public async Task ABareProxy404_IsNotAnAnswer()
        {
            var ex = await SearchPoolsAgainst(HttpStatusCode.NotFound, "<html>not found</html>");
            Assert.That(ex, Is.Not.Null);
            Assert.That(PoolRepository.IsIndexNotFound(ex), Is.False, $"a wrong URL must stay a failed load: {ex}");
        }
    }
}
