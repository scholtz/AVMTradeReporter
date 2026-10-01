using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

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
        public async Task UnknownTimestamp_CoversTheBlock_ButAdvertisesOnlyTheNextKnownOne()
        {
            // a block that could not be fetched has no timestamp: latest-block must never carry another block's timestamp
            var tracker = Create();
            tracker.Seed(100, 1000);
            tracker.MarkCompleted(101, null);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(100, 1000)));
            Assert.That(tracker.CompletedThrough!.Round, Is.EqualTo(101UL), "covered for the stored-through bookkeeping");

            tracker.MarkCompleted(102, 1006);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(102, 1006)));
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
        public async Task RedisMirror_IsWrittenOneAtATime_InOrder_AndNeverGoesBackwards()
        {
            var written = new List<ulong>();
            var firstWriteGate = new TaskCompletionSource();
            var db = new Mock<StackExchange.Redis.IDatabase>();
            db.Setup(d => d.StringSetAsync(It.IsAny<StackExchange.Redis.RedisKey>(), It.IsAny<StackExchange.Redis.RedisValue>(), It.IsAny<StackExchange.Redis.Expiration>(), It.IsAny<StackExchange.Redis.ValueCondition>(), It.IsAny<StackExchange.Redis.CommandFlags>()))
                .Returns(async (StackExchange.Redis.RedisKey _, StackExchange.Redis.RedisValue value, StackExchange.Redis.Expiration _, StackExchange.Redis.ValueCondition _, StackExchange.Redis.CommandFlags _) =>
                {
                    lock (written) written.Add(System.Text.Json.JsonSerializer.Deserialize<IndexedBlock>(value.ToString())!.Round);
                    if (written.Count == 1) await firstWriteGate.Task; // the Seed write is slow while newer blocks complete
                    return true;
                });
            var config = new AppConfiguration();
            config.Redis.Enabled = true;
            config.CoinGecko.LatestBlockVisibilityDelaySeconds = 0;
            var services = new ServiceCollection().AddSingleton(db.Object).BuildServiceProvider();
            var tracker = new IndexedBlockTracker(Options.Create(config), NullLogger<IndexedBlockTracker>.Instance, services);

            tracker.Seed(100, 1000);
            tracker.MarkCompleted(101, 1003);
            tracker.MarkCompleted(102, 1006);
            await Task.Delay(100);
            lock (written) Assert.That(written, Is.EqualTo(new ulong[] { 100 }), "later writes wait for the slow one - no overlap");

            firstWriteGate.SetResult();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) { lock (written) if (written.Count >= 3) break; await Task.Delay(20); }

            lock (written)
            {
                Assert.That(written, Is.Ordered, "Redis must never be left on an older block than the one before it");
                Assert.That(written.Last(), Is.EqualTo(102UL));
            }
        }

        [Test]
        public async Task RedisMirror_IsNotMovedBackwardsByARestartingPodThatSeedsLower()
        {
            var writes = new List<ulong>();
            var db = new Mock<StackExchange.Redis.IDatabase>();
            db.Setup(d => d.StringGetAsync(It.IsAny<StackExchange.Redis.RedisKey>(), It.IsAny<StackExchange.Redis.CommandFlags>()))
                .ReturnsAsync((StackExchange.Redis.RedisValue)System.Text.Json.JsonSerializer.Serialize(new IndexedBlock(500, 5000)));
            db.Setup(d => d.StringSetAsync(It.IsAny<StackExchange.Redis.RedisKey>(), It.IsAny<StackExchange.Redis.RedisValue>(), It.IsAny<StackExchange.Redis.Expiration>(), It.IsAny<StackExchange.Redis.ValueCondition>(), It.IsAny<StackExchange.Redis.CommandFlags>()))
                .Returns((StackExchange.Redis.RedisKey _, StackExchange.Redis.RedisValue value, StackExchange.Redis.Expiration _, StackExchange.Redis.ValueCondition _, StackExchange.Redis.CommandFlags _) =>
                {
                    lock (writes) writes.Add(System.Text.Json.JsonSerializer.Deserialize<IndexedBlock>(value.ToString())!.Round);
                    return Task.FromResult(true);
                });
            var config = new AppConfiguration();
            config.Redis.Enabled = true;
            config.CoinGecko.LatestBlockVisibilityDelaySeconds = 0;
            var tracker = new IndexedBlockTracker(Options.Create(config), NullLogger<IndexedBlockTracker>.Instance, new ServiceCollection().AddSingleton(db.Object).BuildServiceProvider());

            tracker.Seed(100, 1000); // another pod already mirrored 500
            await Task.Delay(100);
            lock (writes) Assert.That(writes, Is.Empty);
            Assert.That((await tracker.GetLatestAsync())!.Round, Is.EqualTo(100UL), "the local value is still this pod's own");
        }

        [Test]
        public async Task Seed_WithoutTimestamp_IsCoveredButNotAdvertised()
        {
            var tracker = Create();
            tracker.Seed(100, null);
            Assert.That(await tracker.GetLatestAsync(), Is.Null, "no made-up timestamp for block 100");
            Assert.That(tracker.CompletedThrough!.Round, Is.EqualTo(100UL));
            tracker.MarkCompleted(101, 1003);
            Assert.That(await tracker.GetLatestAsync(), Is.EqualTo(new IndexedBlock(101, 1003)));
        }

        [Test]
        public async Task UnverifiedSeed_IsNotMirroredToRedis()
        {
            var writes = 0;
            var db = new Mock<StackExchange.Redis.IDatabase>();
            db.Setup(d => d.StringSetAsync(It.IsAny<StackExchange.Redis.RedisKey>(), It.IsAny<StackExchange.Redis.RedisValue>(), It.IsAny<StackExchange.Redis.Expiration>(), It.IsAny<StackExchange.Redis.ValueCondition>(), It.IsAny<StackExchange.Redis.CommandFlags>()))
                .Returns(() => { Interlocked.Increment(ref writes); return Task.FromResult(true); });
            var config = new AppConfiguration();
            config.Redis.Enabled = true;
            config.CoinGecko.LatestBlockVisibilityDelaySeconds = 0;
            var tracker = new IndexedBlockTracker(Options.Create(config), NullLogger<IndexedBlockTracker>.Instance, new ServiceCollection().AddSingleton(db.Object).BuildServiceProvider());

            tracker.Seed(100, 1000, verified: false);
            await Task.Delay(100);
            Assert.That(writes, Is.Zero, "a guessed completion must not become the next restart's truth");
            Assert.That((await tracker.GetLatestAsync())!.Round, Is.EqualTo(100UL), "still advertised locally");
            Assert.That(tracker.CompletedThrough, Is.Null, "nor persisted as StoredThrough");

            tracker.MarkCompleted(101, 1003);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (writes == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.That(writes, Is.EqualTo(1), "a real completion is mirrored");
            Assert.That(tracker.CompletedThrough!.Round, Is.EqualTo(101UL), "and is what the indexer persists");
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
