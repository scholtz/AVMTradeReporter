using AVMTradeReporter.Controllers;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;

namespace AVMTradeReporterTests.Controllers
{
    public class CoinGeckoControllerTests
    {
        private Mock<ICoinGeckoService> _service = null!;
        private AppConfiguration _config = null!;

        [SetUp]
        public void SetUp()
        {
            _service = new Mock<ICoinGeckoService>();
            _config = new AppConfiguration();
        }

        private CoinGeckoController Create() => new(_service.Object, Options.Create(_config))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Test]
        public void Controller_IsPublic_AndRoutedUnderApiCoingecko()
        {
            // CoinGecko's indexer cannot sign ARC-14 tokens
            Assert.That(typeof(CoinGeckoController).GetCustomAttribute<AllowAnonymousAttribute>(), Is.Not.Null);
            Assert.That(typeof(CoinGeckoController).GetCustomAttribute<AuthorizeAttribute>(), Is.Null);
            Assert.That(typeof(CoinGeckoController).GetCustomAttribute<RouteAttribute>()!.Template, Is.EqualTo("api/coingecko"));

            string Route(string method) => typeof(CoinGeckoController).GetMethod(method)!.GetCustomAttribute<HttpGetAttribute>()!.Template!;
            Assert.That(Route(nameof(CoinGeckoController.GetLatestBlock)), Is.EqualTo("latest-block"));
            Assert.That(Route(nameof(CoinGeckoController.GetAsset)), Is.EqualTo("asset"));
            Assert.That(Route(nameof(CoinGeckoController.GetPair)), Is.EqualTo("pair"));
            Assert.That(Route(nameof(CoinGeckoController.GetEvents)), Is.EqualTo("events"));
        }

        [Test]
        public async Task Disabled_AnswersNotFound()
        {
            _config.CoinGecko.Enabled = false;
            var controller = Create();
            Assert.That(await controller.GetLatestBlock(default), Is.TypeOf<NotFoundResult>());
            Assert.That(await controller.GetAsset("0", default), Is.TypeOf<NotFoundResult>());
            Assert.That(await controller.GetPair("1", default), Is.TypeOf<NotFoundResult>());
            Assert.That(await controller.GetEvents(1, 2, default), Is.TypeOf<NotFoundResult>());
        }

        [Test]
        public async Task LatestBlock_Ok_IsShortLivedCacheable()
        {
            _service.Setup(s => s.GetLatestBlockAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CoinGeckoResult<CoinGeckoLatestBlockResponse>.Ok(new CoinGeckoLatestBlockResponse()));
            var controller = Create();
            var result = await controller.GetLatestBlock(default);
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(controller.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=1"));
        }

        [Test]
        public async Task StatusCodes_FollowTheOutcome()
        {
            _service.Setup(s => s.GetAssetAsync("1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.NotFound, "nope"));
            _service.Setup(s => s.GetAssetAsync("x", It.IsAny<CancellationToken>()))
                .ReturnsAsync(CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.BadRequest, "bad"));
            _service.Setup(s => s.GetLatestBlockAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "warming up"));
            var controller = Create();

            Assert.That(await controller.GetAsset("1", default), Is.TypeOf<NotFoundObjectResult>());
            Assert.That(await controller.GetAsset("x", default), Is.TypeOf<BadRequestObjectResult>());
            var unavailable = (ObjectResult)await controller.GetLatestBlock(default);
            Assert.That(unavailable.StatusCode, Is.EqualTo(503));
            Assert.That(controller.Response.Headers.RetryAfter.ToString(), Is.EqualTo("2"));
        }

        [Test]
        public async Task Events_MissingParameters_AreBadRequest()
        {
            var controller = Create();
            Assert.That(await controller.GetEvents(null, 10, default), Is.TypeOf<BadRequestObjectResult>());
            Assert.That(await controller.GetEvents(10, null, default), Is.TypeOf<BadRequestObjectResult>());
            _service.Verify(s => s.GetEventsJsonAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Events_Ok_ReturnsTheCachedJsonBytesAsJson()
        {
            var json = System.Text.Encoding.UTF8.GetBytes("{\"events\":[]}");
            _service.Setup(s => s.GetEventsJsonAsync(5, 9, It.IsAny<CancellationToken>())).ReturnsAsync(CoinGeckoResult<byte[]>.Ok(json));
            var controller = Create();

            var result = await controller.GetEvents(5, 9, default);

            var file = (FileContentResult)result;
            Assert.That(file.ContentType, Is.EqualTo("application/json"));
            Assert.That(file.FileContents, Is.EqualTo(json));
            Assert.That(controller.Response.Headers.CacheControl.ToString(), Does.StartWith("public, max-age="));
        }

        [Test]
        public async Task Events_BadRange_IsBadRequestWithErrorMessage()
        {
            _service.Setup(s => s.GetEventsJsonAsync(9, 5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, "toBlock must be greater than or equal to fromBlock"));
            var result = await Create().GetEvents(9, 5, default);
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }
    }
}
