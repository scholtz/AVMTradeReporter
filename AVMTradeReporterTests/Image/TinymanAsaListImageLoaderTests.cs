using AVMTradeReporter.Processors.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Image
{
    public class TinymanAsaListImageLoaderTests
    {
        [Test]
        public async Task TinymanAsaListImageLoaderTestLoad()
        {
            var loader = new TinymanAsaListImageLoader();
            using var cancellationTokenSource = new CancellationTokenSource();
            var bytes = await loader.LoadImageAsync(0, cancellationTokenSource.Token);
            Assert.That(bytes, Is.Not.Null, "Image bytes should not be null");
            Assert.That(bytes.Length, Is.GreaterThan(100), "Image bytes should not be empty");
        }
    }
}
