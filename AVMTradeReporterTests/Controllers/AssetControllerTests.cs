using AVMTradeReporter.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVMTradeReporterTests.Controllers
{
    public class AssetControllerTests
    {
        private AssetController _controller;

        [SetUp]
        public void Setup()
        {
            _controller = new AssetController(NullLogger<AssetController>.Instance, new MockAssetRepository());
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Test]
        public async Task GetAssetImage_Algo_ReturnsOfficialSvg()
        {
            var result = await _controller.GetAssetImage(0);

            Assert.That(result, Is.TypeOf<FileContentResult>());
            var fileResult = (FileContentResult)result;
            Assert.Multiple(() =>
            {
                Assert.That(fileResult.ContentType, Is.EqualTo("image/svg+xml"));
                Assert.That(fileResult.FileContents.Length, Is.GreaterThan(100));
                Assert.That(System.Text.Encoding.UTF8.GetString(fileResult.FileContents), Does.Contain("algorand-logomark-blue-RGB"));
                Assert.That(_controller.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("public,max-age=604800"));
            });
        }
    }
}
