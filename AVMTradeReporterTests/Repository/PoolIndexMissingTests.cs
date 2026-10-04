using System.Net;
using System.Net.Sockets;
using System.Text;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Voi has no pool, so its Elasticsearch has no 'pools' index. The production client is built with ThrowExceptions(), so the
    /// missing index is thrown - these tests drive the REAL client (same settings) against a local server that answers like
    /// Elasticsearch does, and prove the repository reads it as "answered: empty" and a bare proxy 404 as "load failed".
    /// </summary>
    public class PoolIndexMissingTests
    {
        private const string IndexNotFoundBody =
            "{\"error\":{\"root_cause\":[{\"type\":\"index_not_found_exception\",\"reason\":\"no such index [pools]\",\"resource.type\":\"index_or_alias\",\"resource.id\":\"pools\",\"index_uuid\":\"_na_\",\"index\":\"pools\"}],\"type\":\"index_not_found_exception\",\"reason\":\"no such index [pools]\",\"resource.type\":\"index_or_alias\",\"resource.id\":\"pools\",\"index_uuid\":\"_na_\",\"index\":\"pools\"},\"status\":404}";

        /// <summary>A local stand-in for Elasticsearch: searches answer with the given status/body, everything else (the index template call of the repository constructor) is acknowledged.</summary>
        private sealed class FakeElastic : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _serve;

            public Uri Url { get; }

            public FakeElastic(HttpStatusCode searchStatus, string searchBody)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); // a free port chosen by the OS: no collisions between parallel runs
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = new Uri($"http://127.0.0.1:{port}");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _serve = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await _listener.GetContextAsync(); } catch { return; }
                        var isSearch = ctx.Request.Url!.AbsolutePath.EndsWith("/_search", StringComparison.Ordinal);
                        var bytes = Encoding.UTF8.GetBytes(isSearch ? searchBody : "{\"acknowledged\":true}");
                        ctx.Response.StatusCode = isSearch ? (int)searchStatus : 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers.Add("X-Elastic-Product", "Elasticsearch");
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        ctx.Response.Close();
                    }
                });
            }

            public ElasticsearchClient CreateClient() => new(new ElasticsearchClientSettings(Url)
                // the settings of Program.cs that decide how an error is reported
                .ThrowExceptions()
                .EnableDebugMode()
                .PrettyJson()
                .RequestTimeout(TimeSpan.FromSeconds(10)));

            public void Dispose()
            {
                _listener.Stop();
                _serve.Wait(TimeSpan.FromSeconds(5));
            }
        }

        private static PoolRepository CreateRepository(ElasticsearchClient client)
        {
            var logger = new LoggerFactory().CreateLogger<PoolRepository>();
            var aggregated = new AggregatedPoolRepository(null!, new LoggerFactory().CreateLogger<AggregatedPoolRepository>(), null!, Options.Create(new AppConfiguration()), null!, null, null);
            return new PoolRepository(client, logger, null!, aggregated, new OptionsWrapper<AppConfiguration>(new AppConfiguration()), null!, null, null);
        }

        private static async Task<Exception?> SearchPools(ElasticsearchClient client)
        {
            try
            {
                await client.SearchAsync<PoolModel>(s => s.Indices("pools").Size(10000));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        [Test]
        public async Task TheThrownMissingIndexAnswer_IsRecognised()
        {
            using var elastic = new FakeElastic(HttpStatusCode.NotFound, IndexNotFoundBody);
            var ex = await SearchPools(elastic.CreateClient());
            Assert.That(ex, Is.Not.Null, "ThrowExceptions() must throw for a 404");
            Assert.That(PoolRepository.IsIndexNotFound(ex), Is.True, $"recognised: {ex}");
        }

        [Test]
        public async Task ABareProxy404_IsNotAnAnswer()
        {
            using var elastic = new FakeElastic(HttpStatusCode.NotFound, "<html>not found</html>");
            var ex = await SearchPools(elastic.CreateClient());
            Assert.That(ex, Is.Not.Null);
            Assert.That(PoolRepository.IsIndexNotFound(ex), Is.False, $"a wrong URL must stay a failed load: {ex}");
        }

        [Test]
        public async Task AMissingIndexOnOtherStatuses_IsNotAnAnswer()
        {
            using var elastic = new FakeElastic(HttpStatusCode.ServiceUnavailable, IndexNotFoundBody);
            var ex = await SearchPools(elastic.CreateClient());
            Assert.That(ex, Is.Not.Null);
            Assert.That(PoolRepository.IsIndexNotFound(ex), Is.False, "only a 404 is the index-missing answer");
        }

        [Test]
        public async Task InitializeAsync_AgainstAMissingPoolsIndex_ReportsALoadedEmptyRepository()
        {
            // the Voi regression: no 'pools' index must end as "loaded, nothing stored" (CoinGecko answers 404 / empty events)
            using var elastic = new FakeElastic(HttpStatusCode.NotFound, IndexNotFoundBody);
            var repository = CreateRepository(elastic.CreateClient());

            await repository.InitializeAsync(CancellationToken.None);

            Assert.That(repository.PoolLoadSucceeded, Is.True);
        }

        [Test]
        public async Task InitializeAsync_AgainstAWrongUrl_StaysAFailedLoad()
        {
            using var elastic = new FakeElastic(HttpStatusCode.NotFound, "<html>not found</html>");
            var repository = CreateRepository(elastic.CreateClient());

            await repository.InitializeAsync(CancellationToken.None);

            Assert.That(repository.PoolLoadSucceeded, Is.False, "a proxy 404 is not Elasticsearch answering");
        }
    }
}
