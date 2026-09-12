using Algorand;
using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Services.ScamRating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services.ScamRating
{
    /// <summary>
    /// End-to-end check of the one scam pool identified so far: mainnet app 3680724745 looks like a
    /// Biatec CLAMM pool (same global state keys, `bc` pointing at the real Biatec config app 3074197827)
    /// but runs a foreign 470-byte approval program that is not registered in the ARC-56 registry.
    /// Requires live Algod mainnet + the public registry.
    /// </summary>
    [Category("Live")]
    public class FakeBiatecPoolLiveTests
    {
        private const ulong FakePoolAppId = 3680724745UL;

        [Test]
        public async Task FakeBiatecPool_HashIsUnregistered_AndRatedKnownScam_WithZeroBalances()
        {
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(Algorand.Algod.AlgodConfiguration.MainNet);
            var algod = new DefaultApi(httpClient);
            var app = await algod.GetApplicationByIDAsync(FakePoolAppId);
            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();
            var address = Address.ForApplication(FakePoolAppId).EncodeAsString();

            var registry = new Arc56RegistryClient(new HttpClient(), new OptionsWrapper<AppConfiguration>(new AppConfiguration()), new LoggerFactory().CreateLogger<Arc56RegistryClient>());
            Assert.That(await registry.IsApprovalProgramRegisteredAsync(hash), Is.False, $"approval hash {hash} of app {FakePoolAppId} must not be in the ARC-56 registry");

            var processor = new BiatecPoolProcessor(algod, new MockPoolRepository(), new LoggerFactory().CreateLogger<BiatecPoolProcessor>(), new MockAssetRepository());
            var pool = await processor.LoadPoolAsync(address, FakePoolAppId);
            Assert.That(pool.ApprovalProgramHash, Is.EqualTo(hash));
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Biatec));

            var service = new ScamRatingService(registry, new OptionsWrapper<AppConfiguration>(new AppConfiguration()), new LoggerFactory().CreateLogger<ScamRatingService>());
            await service.ApplyAsync(pool);

            Assert.Multiple(() =>
            {
                Assert.That(pool.ScamRating, Is.EqualTo(100));
                Assert.That(pool.A, Is.EqualTo(0UL));
                Assert.That(pool.B, Is.EqualTo(0UL));
                Assert.That(pool.RealAmountA, Is.EqualTo(0m));
                Assert.That(pool.RealAmountB, Is.EqualTo(0m));
            });
        }
    }
}
