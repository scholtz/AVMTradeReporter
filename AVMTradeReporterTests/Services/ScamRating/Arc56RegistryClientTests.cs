using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services.ScamRating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace AVMTradeReporterTests.Services.ScamRating
{
    /// <summary>
    /// <see cref="Arc56RegistryClient"/> against a scripted HTTP handler (URL shape, status handling,
    /// caching) plus two live checks against the public registry.
    /// </summary>
    public class Arc56RegistryClientTests
    {
        /// <summary>
        /// SHA-256 of the compiled BiatecClammPool approval program
        /// (BiatecCLAMM/contracts/artifacts/BiatecClammPool.arc56.json byteCode.approval) - indexed by the registry.
        /// </summary>
        private const string BiatecClammPoolApprovalHash = "d5af0104faa636835883acc9096d9908b4a1e2caa6fe8d662e62b141ec8b41b1";

        /// <summary>
        /// SHA-256 of the 470-byte approval program of mainnet app 3680724745 - a fake pool that mimics the
        /// global state of a Biatec CLAMM pool but is not the Biatec contract and has no ARC-56 spec.
        /// </summary>
        private const string FakeBiatecPoolApprovalHash = "b30a4c3afb4c28418592ac45dd6cc2179cb3297f2aee18e9ea02d3ab1d836c73";

        private sealed class FakeTimeProvider : TimeProvider
        {
            private DateTimeOffset _now;
            public FakeTimeProvider(DateTimeOffset now) { _now = now; }
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = new();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                return Task.FromResult(Respond(request));
            }
        }

        private static Arc56RegistryClient MakeClient(HttpMessageHandler handler, ScamRatingConfiguration? config = null, TimeProvider? time = null)
        {
            var appConfig = new AppConfiguration();
            if (config != null) appConfig.ScamRating = config;
            return new Arc56RegistryClient(new HttpClient(handler), new OptionsWrapper<AppConfiguration>(appConfig), new LoggerFactory().CreateLogger<Arc56RegistryClient>(), time);
        }

        [Test]
        public void BuildApprovalProgramUrl_UsesFirstThreeHexCharsAsFolder()
        {
            var url = Arc56RegistryClient.BuildApprovalProgramUrl("https://scholtz.github.io/ARC56Registry/", FakeBiatecPoolApprovalHash.ToUpperInvariant());

            Assert.That(url, Is.EqualTo($"https://scholtz.github.io/ARC56Registry/approval-programs/b30/{FakeBiatecPoolApprovalHash}.txt"));
        }

        [Test]
        public async Task Http200_MeansRegistered()
        {
            var handler = new ScriptedHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("https://raw.githubusercontent.com/x/y.arc56.json") } };
            var client = MakeClient(handler);

            var registered = await client.IsApprovalProgramRegisteredAsync(BiatecClammPoolApprovalHash);

            Assert.Multiple(() =>
            {
                Assert.That(registered, Is.True);
                Assert.That(handler.Requests, Has.Count.EqualTo(1));
                Assert.That(handler.Requests[0].ToString(), Is.EqualTo($"https://scholtz.github.io/ARC56Registry/approval-programs/d5a/{BiatecClammPoolApprovalHash}.txt"));
            });
        }

        [Test]
        public async Task Http404_MeansNotRegistered()
        {
            var client = MakeClient(new ScriptedHandler());

            Assert.That(await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash), Is.False);
        }

        [Test]
        public async Task ServerError_IsUnknownNotUnregistered()
        {
            var handler = new ScriptedHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
            var client = MakeClient(handler);

            Assert.That(await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash), Is.Null);
        }

        [Test]
        public async Task NetworkFailure_IsUnknownNotUnregistered()
        {
            var handler = new ScriptedHandler { Respond = _ => throw new HttpRequestException("dns") };
            var client = MakeClient(handler);

            Assert.That(await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash), Is.Null);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not-a-hash")]
        [TestCase("d5af0104")]
        public async Task MalformedHash_IsUnknownAndNotRequested(string hash)
        {
            var handler = new ScriptedHandler();
            var client = MakeClient(handler);

            Assert.Multiple(async () =>
            {
                Assert.That(await client.IsApprovalProgramRegisteredAsync(hash), Is.Null);
                Assert.That(handler.Requests, Is.Empty);
            });
        }

        [Test]
        public async Task LookupDisabled_IsUnknownAndNotRequested()
        {
            var handler = new ScriptedHandler();
            var client = MakeClient(handler, new ScamRatingConfiguration { RegistryLookupEnabled = false });

            Assert.Multiple(async () =>
            {
                Assert.That(await client.IsApprovalProgramRegisteredAsync(BiatecClammPoolApprovalHash), Is.Null);
                Assert.That(handler.Requests, Is.Empty);
            });
        }

        [Test]
        public async Task RegisteredAnswer_IsCachedForever()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-12T00:00:00Z"));
            var handler = new ScriptedHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) };
            var client = MakeClient(handler, time: time);

            await client.IsApprovalProgramRegisteredAsync(BiatecClammPoolApprovalHash);
            time.Advance(TimeSpan.FromDays(365));
            var again = await client.IsApprovalProgramRegisteredAsync(BiatecClammPoolApprovalHash);

            Assert.Multiple(() =>
            {
                Assert.That(again, Is.True);
                Assert.That(handler.Requests, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task NotRegisteredAnswer_IsReCheckedAfterCacheWindow()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-12T00:00:00Z"));
            var handler = new ScriptedHandler();
            var client = MakeClient(handler, new ScamRatingConfiguration { NotRegisteredCacheHours = 24 }, time);

            await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash);
            time.Advance(TimeSpan.FromHours(23));
            await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash);
            Assert.That(handler.Requests, Has.Count.EqualTo(1), "within the cache window the registry is not asked again");

            // the registry is regenerated daily - the contract's spec got published in the meantime
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
            time.Advance(TimeSpan.FromHours(2));
            var registered = await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash);

            Assert.Multiple(() =>
            {
                Assert.That(registered, Is.True);
                Assert.That(handler.Requests, Has.Count.EqualTo(2));
            });
        }

        [Test]
        [Category("Live")]
        public async Task Live_BiatecClammPoolContract_IsInTheArc56Registry()
        {
            var client = new Arc56RegistryClient(new HttpClient(), new OptionsWrapper<AppConfiguration>(new AppConfiguration()), new LoggerFactory().CreateLogger<Arc56RegistryClient>());

            var registered = await client.IsApprovalProgramRegisteredAsync(BiatecClammPoolApprovalHash);

            Assert.That(registered, Is.True, "the real Biatec CLAMM pool contract must resolve in https://scholtz.github.io/ARC56Registry/");
        }

        [Test]
        [Category("Live")]
        public async Task Live_FakeBiatecPool3680724745Contract_IsNotInTheArc56Registry()
        {
            var client = new Arc56RegistryClient(new HttpClient(), new OptionsWrapper<AppConfiguration>(new AppConfiguration()), new LoggerFactory().CreateLogger<Arc56RegistryClient>());

            var registered = await client.IsApprovalProgramRegisteredAsync(FakeBiatecPoolApprovalHash);

            Assert.That(registered, Is.False, "the fake pool's approval program has no public ARC-56 spec");
        }
    }
}
