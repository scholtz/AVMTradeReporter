using Algorand.Algod;
using Algorand.Algod.Model;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AVMTradeReporterTests.Repository
{
    public class AssetRepositoryTests
    {
        [Test]
        public async Task GetAssetAsync_DestroyedAsset_CachesTombstoneAndQueriesAlgodOnlyOnce()
        {
            // Unique id so the static asset cache shared across tests can't interfere.
            const ulong assetId = 999_888_777_666UL;
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            algod
                .Setup(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), assetId))
                .ThrowsAsync(new Algorand.ApiException<ErrorResponse>(
                    "asset does not exist",
                    404,
                    "asset does not exist",
                    new Dictionary<string, IEnumerable<string>>(),
                    new ErrorResponse(),
                    null));

            var repo = new AssetRepository(algod.Object, NullLogger<AssetRepository>.Instance);

            var first = await repo.GetAssetAsync(assetId);
            var second = await repo.GetAssetAsync(assetId);

            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            algod.Verify(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), assetId), Times.Once);
        }

        [Test]
        public async Task GetAssetAsync_TransientAlgodError_IsNotCachedAsTombstone()
        {
            const ulong assetId = 999_888_777_667UL;
            var algod = new Mock<IDefaultApi>(MockBehavior.Strict);
            algod
                .Setup(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), assetId))
                .ThrowsAsync(new Algorand.ApiException(
                    "internal error",
                    500,
                    "internal error",
                    new Dictionary<string, IEnumerable<string>>(),
                    null));

            var repo = new AssetRepository(algod.Object, NullLogger<AssetRepository>.Instance);

            var first = await repo.GetAssetAsync(assetId);
            var second = await repo.GetAssetAsync(assetId);

            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            // A transient failure must not be tombstoned - the next call should retry algod.
            algod.Verify(a => a.GetAssetByIDAsync(It.IsAny<CancellationToken>(), assetId), Times.Exactly(2));
        }
    }
}
