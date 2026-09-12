using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services.ScamRating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AVMTradeReporterTests.Services.ScamRating
{
    /// <summary>
    /// Scoring rules of <see cref="ScamRatingService"/> with a mocked ARC-56 registry.
    /// </summary>
    public class ScamRatingServiceTests
    {
        /// <summary>
        /// The fake "Biatec CLAMM" pool on Algorand mainnet (see <see cref="ScamRatingConfiguration.KnownScamPools"/>).
        /// </summary>
        private const ulong KnownScamPoolAppId = 3680724745UL;
        private const string RegisteredHash = "d5af0104faa636835883acc9096d9908b4a1e2caa6fe8d662e62b141ec8b41b1";
        private const string UnregisteredHash = "b30a4c3afb4c28418592ac45dd6cc2179cb3297f2aee18e9ea02d3ab1d836c73";

        private static ScamRatingService MakeService(Mock<IArc56RegistryClient> registry, ScamRatingConfiguration? config = null)
        {
            var appConfig = new AppConfiguration();
            if (config != null) appConfig.ScamRating = config;
            return new ScamRatingService(registry.Object, new OptionsWrapper<AppConfiguration>(appConfig), new LoggerFactory().CreateLogger<ScamRatingService>());
        }

        private static Mock<IArc56RegistryClient> MakeRegistry(bool? answer)
        {
            var registry = new Mock<IArc56RegistryClient>(MockBehavior.Strict);
            registry
                .Setup(r => r.IsApprovalProgramRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(answer);
            return registry;
        }

        private static AVMTradeReporter.Models.Data.Pool MakePool(ulong appId, string? hash) => new()
        {
            PoolAddress = "POOL" + appId,
            PoolAppId = appId,
            Protocol = DEXProtocol.Biatec,
            AMMType = AMMType.ConcentratedLiquidityAMM,
            ApprovalProgramHash = hash,
            A = 1_000_000_000,
            B = 2_000_000_000,
        };

        [Test]
        public void DefaultConfiguration_ListsTheFakeBiatecClammPoolAsKnownScam()
        {
            var config = new ScamRatingConfiguration();

            Assert.Multiple(() =>
            {
                Assert.That(config.KnownScamPools, Contains.Key(KnownScamPoolAppId));
                Assert.That(config.KnownScamPools[KnownScamPoolAppId], Is.EqualTo(ScamRatingPolicy.KnownScamRating));
                Assert.That(config.UnregisteredContractPoints, Is.EqualTo(5));
                Assert.That(config.Arc56RegistryBaseUrl, Is.EqualTo("https://scholtz.github.io/ARC56Registry"));
            });
        }

        [Test]
        public async Task RegisteredContract_ScoresZero()
        {
            var service = MakeService(MakeRegistry(true));

            var rating = await service.ComputeScamRatingAsync(MakePool(1, RegisteredHash));

            Assert.That(rating, Is.EqualTo(0));
        }

        [Test]
        public async Task ContractNotInArc56Registry_AddsFivePoints()
        {
            var service = MakeService(MakeRegistry(false));

            var rating = await service.ComputeScamRatingAsync(MakePool(1, UnregisteredHash));

            Assert.That(rating, Is.EqualTo(5));
        }

        [Test]
        public async Task ContractNotInArc56Registry_UsesConfiguredPoints()
        {
            var service = MakeService(MakeRegistry(false), new ScamRatingConfiguration { UnregisteredContractPoints = 12 });

            var rating = await service.ComputeScamRatingAsync(MakePool(1, UnregisteredHash));

            Assert.That(rating, Is.EqualTo(12));
        }

        [Test]
        public async Task RegistryUnavailable_DoesNotPenalise()
        {
            var service = MakeService(MakeRegistry(null));

            var rating = await service.ComputeScamRatingAsync(MakePool(1, UnregisteredHash));

            Assert.That(rating, Is.EqualTo(0));
        }

        [Test]
        public async Task PoolWithoutHash_IsNotLookedUpAndScoresZero()
        {
            var registry = new Mock<IArc56RegistryClient>(MockBehavior.Strict); // any call would throw
            var service = MakeService(registry);

            var rating = await service.ComputeScamRatingAsync(MakePool(1, null));

            Assert.That(rating, Is.EqualTo(0));
            registry.VerifyNoOtherCalls();
        }

        [Test]
        public async Task KnownScamPool_Scores100RegardlessOfRegistry()
        {
            var registry = new Mock<IArc56RegistryClient>(MockBehavior.Strict); // must not even be consulted
            var service = MakeService(registry);

            var rating = await service.ComputeScamRatingAsync(MakePool(KnownScamPoolAppId, RegisteredHash));

            Assert.That(rating, Is.EqualTo(100));
            registry.VerifyNoOtherCalls();
        }

        [Test]
        public async Task KnownScamPoolRating_IsClampedTo100()
        {
            var config = new ScamRatingConfiguration { KnownScamPools = new() { { 7UL, 999 } } };
            var service = MakeService(MakeRegistry(true), config);

            Assert.That(await service.ComputeScamRatingAsync(MakePool(7, RegisteredHash)), Is.EqualTo(100));
        }

        [Test]
        public async Task Apply_KnownScamPool_SetsRatingAndZeroesBalances()
        {
            var service = MakeService(MakeRegistry(true));
            var pool = MakePool(KnownScamPoolAppId, UnregisteredHash);

            var changed = await service.ApplyAsync(pool);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.True);
                Assert.That(pool.ScamRating, Is.EqualTo(100));
                Assert.That(pool.A, Is.EqualTo(0UL));
                Assert.That(pool.B, Is.EqualTo(0UL));
                Assert.That(pool.RealAmountA, Is.EqualTo(0m));
                Assert.That(pool.RealAmountB, Is.EqualTo(0m));
            });
        }

        [Test]
        public async Task Apply_UnregisteredContract_SetsRatingFiveAndKeepsBalances()
        {
            var service = MakeService(MakeRegistry(false));
            var pool = MakePool(1, UnregisteredHash);

            var changed = await service.ApplyAsync(pool);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.True);
                Assert.That(pool.ScamRating, Is.EqualTo(5));
                Assert.That(pool.A, Is.EqualTo(1_000_000_000UL));
                Assert.That(pool.B, Is.EqualTo(2_000_000_000UL));
            });
        }

        [Test]
        public async Task Apply_RegisteredContract_ReportsNoChange()
        {
            var service = MakeService(MakeRegistry(true));
            var pool = MakePool(1, RegisteredHash);

            var changed = await service.ApplyAsync(pool);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.False);
                Assert.That(pool.ScamRating, Is.EqualTo(0));
                Assert.That(pool.A, Is.EqualTo(1_000_000_000UL));
            });
        }

        [Test]
        public async Task Apply_RegistryStartsKnowingTheContractLater_ClearsThePenalty()
        {
            var registry = new Mock<IArc56RegistryClient>(MockBehavior.Strict);
            var answers = new Queue<bool?>(new bool?[] { false, true });
            registry
                .Setup(r => r.IsApprovalProgramRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => answers.Dequeue());
            var service = MakeService(registry);
            var pool = MakePool(1, RegisteredHash);

            await service.ApplyAsync(pool);
            Assert.That(pool.ScamRating, Is.EqualTo(5));

            await service.ApplyAsync(pool);
            Assert.That(pool.ScamRating, Is.EqualTo(0));
        }
    }
}
