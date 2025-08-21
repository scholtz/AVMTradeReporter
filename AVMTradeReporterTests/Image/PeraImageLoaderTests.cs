using AVMTradeReporter.Processors.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Image
{
    public class PeraImageLoaderTests
    {
        [Test]
        public async Task PeraImageLoaderTestLoad()
        {
            var loader = new PeraImageLoader();
            using var cancellationTokenSource = new CancellationTokenSource();
            var bytes = await loader.LoadImageAsync(3054226103, cancellationTokenSource.Token);
            Assert.That(bytes, Is.Not.Null, "Image bytes should not be null");
            Assert.That(bytes.Length, Is.GreaterThan(100), "Image bytes should not be empty");
        }
    }
}
