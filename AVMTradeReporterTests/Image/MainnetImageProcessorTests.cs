﻿using AVMTradeReporter.Processors.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Image
{
    public class MainnetImageProcessorTests
    {
        [Test]
        public async Task LoadImageForAsset0Async()
        {
            // Arrange
            ulong assetId = 0; // Example asset ID
            var processor = new MainnetImageProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.IsNotNull(imageData, "Image data should not be null");
            Assert.IsTrue(imageData.Length > 100, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset1241945177Async()
        {
            // this is loaded from tinyman list

            // Arrange
            ulong assetId = 1241945177; // Example asset ID
            var processor = new MainnetImageProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.IsNotNull(imageData, "Image data should not be null");
            Assert.IsTrue(imageData.Length > 100, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset3054226103Async()
        {
            // this is loaded from pera

            // Arrange
            ulong assetId = 3054226103; // Example asset ID
            var processor = new MainnetImageProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.IsNotNull(imageData, "Image data should not be null");
            Assert.IsTrue(imageData.Length > 100, "Image data should not be empty");
        }
        [Test]
        public async Task LoadImageForAsset123Async()
        {
            // non existent asset, should return empty image

            // Arrange
            ulong assetId = 123; // Example asset ID
            var processor = new MainnetImageProcessor();
            using var cancellationTokenSource = new CancellationTokenSource();
            // Act
            var imageData = await processor.LoadImageAsync(assetId, cancellationTokenSource.Token);
            // Assert
            Assert.IsNotNull(imageData, "Image data should not be null");
            Assert.IsTrue(imageData.Length > 0, "Image data should not be empty");
            Assert.IsTrue(imageData.Length < 1000, "Image data should not be empty");
        }
    }
}
