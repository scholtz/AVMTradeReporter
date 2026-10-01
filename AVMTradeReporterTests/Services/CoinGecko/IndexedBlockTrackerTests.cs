using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class IndexedBlockTrackerTests
    {
        private static IndexedBlockTracker Create(int delaySeconds = 0)
        {
            var config = new AppConfiguration();
            config.Redis.Enabled = false;
            config.CoinGecko.LatestBlockVisibilityDelaySeconds = delaySeconds;
            return new IndexedBlockTracker(Options.Create(config), NullLogger<IndexedBlockTracker>.Instance, new ServiceCollection().BuildServiceProvider());
        }

        [Test]
        public async Task Unknown_UntilSeeded()
        {
            var tracker = Create();
            tracker.MarkCompleted(100, 1000);
            Assert.That(await tracker.GetLatestAsync(), Is.Null, "an unseeded (e.g. backward) indexer must never claim a watermark");
        }

        [Test]
        public async Task Seed_PublishesImmediately()
        {
            var tracker = Create(delaySeconds: 30);
            tracker.Seed(99, 990);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(99, 990)));
        }

        [Test]
        public async Task InOrderCompletions_AdvanceTheWatermark()
        {
            var tracker = Create();
            tracker.Seed(99, 990);
            tracker.MarkCompleted(100, 1000);
            tracker.MarkCompleted(101, 1003);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(101, 1003)));
        }

        [Test]
        public async Task OutOfOrderCompletion_DoesNotLeapOverAnUnfinishedBlock()
        {
            // blocks are processed concurrently: 102 and 103 finish before 101 - events of 101 are not stored yet
            var tracker = Create();
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(103, 1009);
            tracker.MarkCompleted(102, 1006);
            Assert.That((await tracker.GetLatestAsync())!.Round, Is.EqualTo(100UL), "block 101 is still being processed");

            tracker.MarkCompleted(101, 1003);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(103, 1009)));
        }

        [Test]
        public async Task StaleOrDuplicateCompletions_AreIgnored()
        {
            var tracker = Create();
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(100, 5);
            tracker.MarkCompleted(50, 5);
            tracker.MarkCompleted(101, 1003);
            tracker.MarkCompleted(101, 9999);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(101, 1003)));
        }

        [Test]
        public async Task MissingTimestamp_CarriesThePreviousOne()
        {
            var tracker = Create();
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(101, null);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(101, 1000)));
        }

        [Test]
        public async Task NewBlock_IsHeldBackForTheVisibilityDelay()
        {
            // Elasticsearch needs ~1 s to make fresh documents searchable; latest-block must not run ahead of that
            var tracker = Create(delaySeconds: 1);
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(101, 1003);
            Assert.That((await tracker.GetLatestAsync())!.Round, Is.EqualTo(100UL));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await tracker.GetLatestAsync())!.Round < 101 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(101, 1003)));
        }

        [Test]
        public async Task Seed_DoesNotMoveTheWatermarkBackwards()
        {
            var tracker = Create();
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(101, 1003);
            tracker.Seed(50, 500);
            Assert.That((await tracker.GetLatestAsync())!.Round, Is.EqualTo(101UL));
        }
    }
}
