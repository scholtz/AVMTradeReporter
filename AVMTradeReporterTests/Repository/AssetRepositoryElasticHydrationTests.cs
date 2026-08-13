using Algorand.Algod;
using Algorand.Algod.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// HA/restart durability for the asset cache (production incident, 2026-08-13):
    ///
    /// Every deploy of AVMTradeReporter wiped the token list on the Assets page until live
    /// trades slowly re-populated it. Root cause: BiatecAsset records (params + computed
    /// PriceUSD/TVL_USD/PoolsCount/volumes) had exactly one durable store - Redis - and the
    /// production API runs with Redis.Enabled=false, so the only copy lived in the pod's
    /// static in-memory dictionary. Pools survive restarts because PoolRepository falls back
    /// to Elasticsearch when Redis yields nothing; AssetRepository had no Elasticsearch leg
    /// at all.
    ///
    /// Contract under test: assets are persisted to Elasticsearch (as AssetSnapshot envelopes
    /// carrying the same System.Text.Json payload as the Redis path) on every write, and
    /// EnsureInitializedAsync hydrates the in-memory cache from Elasticsearch whenever Redis
    /// is disabled, empty, or freshly restarted - so a pod restart has no effect on the data.
    ///
    /// The tests override the two internal virtual Elasticsearch seams with an in-memory
    /// store, so the real serialization round-trip (System.Text.Json of BiatecAsset including
    /// Algorand Address fields) is exercised while no Elasticsearch client is needed.
    /// </summary>
    public class AssetRepositoryElasticHydrationTests
    {
        private sealed class TestAssetRepository : AssetRepository
        {
            public Dictionary<ulong, AssetSnapshot> EsStore { get; }
            public int LoadCalls { get; private set; }

            public TestAssetRepository(IDefaultApi algod, Dictionary<ulong, AssetSnapshot> esStore)
                : base(algod, NullLogger<AssetRepository>.Instance)
            {
                EsStore = esStore;
            }

            internal override Task SaveSnapshotToElasticsearchAsync(AssetSnapshot snapshot, CancellationToken cancellationToken)
            {
                EsStore[snapshot.Id] = snapshot;
                return Task.CompletedTask;
            }

            internal override Task<IReadOnlyCollection<AssetSnapshot>> LoadSnapshotsFromElasticsearchAsync(CancellationToken cancellationToken)
            {
                LoadCalls++;
                return Task.FromResult<IReadOnlyCollection<AssetSnapshot>>(EsStore.Values.ToArray());
            }
        }

        [SetUp]
        public void ResetSharedStaticCache() => AssetRepository.ResetForTests();

        [TearDown]
        public void CleanupSharedStaticCache() => AssetRepository.ResetForTests();

        private static BiatecAsset MakeAsset(ulong index, string name, decimal priceUsd, int poolsCount) => new()
        {
            Index = index,
            PriceUSD = priceUsd,
            TVL_USD = 29159.77m,
            TotalTVLAssetInUSD = 69513.09m,
            PoolsCount = poolsCount,
            PriceUSD24H = 0.9025m,
            Volume24H = 351.38m,
            Timestamp = DateTimeOffset.UtcNow,
            Params = new AssetParams
            {
                Name = name,
                UnitName = name,
                Decimals = 6,
                Total = 1_000_000_000_000_000,
                DefaultFrozen = false,
            },
        };

        [Test]
        public async Task Restart_WithoutRedis_AssetListSurvivesViaElasticsearch()
        {
            // Strict mock with zero setups: any algod call fails the test, proving hydration
            // works entirely from the durable store without re-fetching from the chain.
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            var esStore = new Dictionary<ulong, AssetSnapshot>();

            var repoBeforeRestart = new TestAssetRepository(algod.Object, esStore);
            await repoBeforeRestart.SetAssetAsync(MakeAsset(1241945177UL, "GoldDAO", 0.8955m, 8));
            Assert.That(esStore, Contains.Key(1241945177UL), "SetAssetAsync must persist the asset to Elasticsearch");

            // Simulate a pod restart: the static in-memory cache is wiped, a fresh repository
            // instance boots against the same durable Elasticsearch content.
            AssetRepository.ResetForTests();
            var repoAfterRestart = new TestAssetRepository(algod.Object, esStore);

            var listed = (await repoAfterRestart.GetAssetsAsync(null, null, 0, 100, CancellationToken.None)).ToArray();

            Assert.That(listed, Has.Length.EqualTo(1), "the browse listing must be non-empty immediately after restart");
            var asset = listed[0];
            Assert.That(asset.Index, Is.EqualTo(1241945177UL));
            Assert.That(asset.Params?.Name, Is.EqualTo("GoldDAO"));
            Assert.That(asset.PriceUSD, Is.EqualTo(0.8955m), "computed price must survive the restart");
            Assert.That(asset.TVL_USD, Is.EqualTo(29159.77m), "computed TVL must survive the restart");
            Assert.That(asset.PoolsCount, Is.EqualTo(8), "PoolsCount must survive - the browse listing filters on it");
        }

        [Test]
        public async Task Restart_TombstonesSurvive_AndStillSuppressAlgodLookups()
        {
            const ulong destroyedId = 999_888_777_555UL;
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            algod
                .Setup(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), destroyedId))
                .ThrowsAsync(new Algorand.ApiException<ErrorResponse>(
                    "asset does not exist", 404, "asset does not exist",
                    new Dictionary<string, IEnumerable<string>>(), new ErrorResponse(), null));

            var esStore = new Dictionary<ulong, AssetSnapshot>();
            var repoBeforeRestart = new TestAssetRepository(algod.Object, esStore);
            Assert.That(await repoBeforeRestart.GetAssetAsync(destroyedId), Is.Null);
            Assert.That(esStore, Contains.Key(destroyedId), "the tombstone must be persisted to Elasticsearch");

            AssetRepository.ResetForTests();
            var repoAfterRestart = new TestAssetRepository(algod.Object, esStore);

            Assert.That(await repoAfterRestart.GetAssetAsync(destroyedId), Is.Null);
            // One call from before the restart, none after - the hydrated tombstone suppresses the retry.
            algod.Verify(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), destroyedId), Times.Once);
        }

        [Test]
        public async Task Hydration_DoesNotOverwriteAssetsAlreadyWrittenThisSession()
        {
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            var esStore = new Dictionary<ulong, AssetSnapshot>();

            // Durable store holds a stale copy (old price) from before the restart.
            var stale = new TestAssetRepository(algod.Object, esStore);
            await stale.SetAssetAsync(MakeAsset(555_001UL, "STALE", 1.00m, 3));
            AssetRepository.ResetForTests();

            // After the restart a live update lands BEFORE initialization finishes scanning the
            // durable store - the fresher in-memory record must win over the stale snapshot.
            var repo = new TestAssetRepository(algod.Object, esStore);
            await repo.SetAssetAsync(MakeAsset(555_001UL, "STALE", 2.00m, 3));

            var asset = await repo.GetAssetAsync(555_001UL);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset!.PriceUSD, Is.EqualTo(2.00m), "a fresher live write must not be clobbered by hydration");
        }

        [Test]
        public async Task SetAssetAsync_WithoutElasticsearchOrRedis_StillWorks()
        {
            // Environments with neither Redis nor Elasticsearch (local dev, unit tests) must
            // keep working purely in-memory - persistence is best-effort, never a hard dependency.
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            var repo = new AssetRepository(algod.Object, NullLogger<AssetRepository>.Instance);

            await repo.SetAssetAsync(MakeAsset(555_002UL, "MEMONLY", 3.00m, 1));

            var asset = await repo.GetAssetAsync(555_002UL);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset!.PriceUSD, Is.EqualTo(3.00m));
        }
    }
}
