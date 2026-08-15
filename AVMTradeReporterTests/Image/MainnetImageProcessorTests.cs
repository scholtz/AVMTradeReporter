using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Processors.Image;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Image
{
    public class MainnetImageProcessorTests
    {
        private static MainnetImageProcessor CreateProcessor() =>
            new MainnetImageProcessor(new MockAssetRepository(), Options.Create(new AppConfiguration()));

        [Test]
        public async Task LoadImageForAsset0Async()
        {
            // Arrange
            ulong assetId = 0; // Example asset ID
            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.That(imageData, Is.Not.Null, "Image data should not be null");
            Assert.That(imageData.Length > 100, Is.True, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset1241945177Async()
        {
            // this is loaded from tinyman list

            // Arrange
            ulong assetId = 1241945177; // Example asset ID
            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.That(imageData, Is.Not.Null, "Image data should not be null");
            Assert.That(imageData.Length > 100, Is.True, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset3054226103Async()
        {
            // this is loaded from pera

            // Arrange
            ulong assetId = 3054226103; // Example asset ID
            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.That(imageData, Is.Not.Null, "Image data should not be null");
            Assert.That(imageData.Length > 100, Is.True, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset123Async()
        {
            // non existent asset, should return empty image

            // Arrange
            ulong assetId = 123; // Example asset ID
            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.That(imageData, Is.Not.Null, "Image data should not be null");
            Assert.That(imageData.Length > 0, Is.True, "Image data should not be empty");
            Assert.That(imageData.Length < 1000, Is.True, "Image data should not be empty");
        }

        private static string CacheFilePath(ulong assetId) =>
            Path.Combine(AppContext.BaseDirectory, "images", "mainnet-v1.0", $"{assetId}.png");

        [Test]
        public async Task StaleWrongCachedIconIsRefreshedFromSourceAsync()
        {
            // Reproduces the Meld Gold / ASA.Gold ticker-collision mixup (docs/ICON_SHARING.md):
            // asset 246516580 (Meld Gold) had another project's icon wrongly written into its
            // id-keyed cache file. Once that cached file is older than the refresh TTL, the next
            // request must re-resolve from Tinyman/Pera and overwrite it with the real icon.
            ulong assetId = 246516580;
            var wrongImage = new byte[2000];
            new Random(1).NextBytes(wrongImage);

            var path = CacheFilePath(assetId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, wrongImage);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();

            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);

            Assert.That(imageData, Is.Not.EqualTo(wrongImage), "Stale wrong icon should have been replaced by a fresh resolution");
            Assert.That(imageData.Length > 100, Is.True, "Refreshed image data should not be empty");
        }

        [Test]
        public async Task StaleCacheIsKeptWhenRefreshFindsNothingUsableAsync()
        {
            // A non-existent asset id can't resolve from Tinyman/Pera, so a failed refresh
            // attempt must never destroy the previously cached (still non-empty) icon.
            ulong assetId = 999999999;
            var oldImage = new byte[2000];
            new Random(2).NextBytes(oldImage);

            var path = CacheFilePath(assetId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, oldImage);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

            var processor = CreateProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();

            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);

            Assert.That(imageData, Is.EqualTo(oldImage), "Old cached icon should be kept when refresh finds nothing usable");
        }
    }
}
