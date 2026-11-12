using AVMTradeReporter.HealthChecks;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// 2026-11 mainnet incident: a block task hung, the latest-block watermark froze for 4.7 days and the fetch loop
    /// stopped a few days later, while /health stayed green. The stall must turn /health unhealthy (so the liveness
    /// probe restarts the pod, which resumes at the hung block), and nothing legitimate may.
    /// </summary>
    [TestFixture]
    public class IndexerStallMonitorTests
    {
        private sealed class FakeTime : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-07T19:36:04Z");
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

        private FakeTime _time = null!;
        private IndexerStallMonitor _monitor = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _monitor = new IndexerStallMonitor(_time);
        }

        [Test]
        public void NothingStarted_IsNotAStall()
        {
            // a pod whose indexer never ran (still warming up, or it does not index at all)
            _time.Advance(TimeSpan.FromDays(5));
            Assert.That(_monitor.DescribeStall(Threshold), Is.Null);
        }

        [Test]
        public void BlockThatNeverReturns_IsAStall_EvenWhileTheLoopKeepsTurning()
        {
            // the incident: block 66736698 hung in one of the concurrent slots, the loop went on with the others
            _monitor.Heartbeat();
            _monitor.BlockStarted(66736698);
            for (var i = 0; i < 31; i++)
            {
                _time.Advance(TimeSpan.FromMinutes(1));
                _monitor.Heartbeat();
                _monitor.BlockStarted(66736699UL + (ulong)i);
                _monitor.BlockFinished(66736699UL + (ulong)i);
            }

            var stall = _monitor.DescribeStall(Threshold);
            Assert.That(stall, Does.Contain("66736698"), "names the hung block");
        }

        [Test]
        public void BlocksThatFinish_AreNeverAStall()
        {
            for (ulong round = 1; round <= 100; round++)
            {
                _monitor.Heartbeat();
                _monitor.BlockStarted(round);
                _time.Advance(TimeSpan.FromMinutes(1)); // slow, but each one returns
                _monitor.BlockFinished(round);
            }
            Assert.That(_monitor.DescribeStall(Threshold), Is.Null);
        }

        [Test]
        public void SlowBlockBelowTheThreshold_IsNotAStall()
        {
            _monitor.Heartbeat();
            _monitor.BlockStarted(7);
            _time.Advance(Threshold - TimeSpan.FromSeconds(1));
            Assert.That(_monitor.DescribeStall(Threshold), Is.Null);
        }

        [Test]
        public void SilentLoop_IsAStall()
        {
            // nothing in flight, but the loop itself stopped turning (it awaits something that never completes)
            _monitor.Heartbeat();
            _time.Advance(Threshold + TimeSpan.FromMinutes(1));
            Assert.That(_monitor.DescribeStall(Threshold), Does.Contain("loop"));
        }

        [Test]
        public void LoopStoppedOnPurpose_IsNotAStall()
        {
            _monitor.Heartbeat();
            _monitor.BlockStarted(7); // abandoned by the shutdown
            _monitor.LoopStopped();
            _time.Advance(TimeSpan.FromDays(1));
            Assert.That(_monitor.DescribeStall(Threshold), Is.Null);
        }

        [Test]
        public void BlockHandedToTheLostBlockRetry_IsWatchedAgainOnlyWhileItIsProcessed()
        {
            _monitor.Heartbeat();
            _monitor.BlockStarted(7);
            _monitor.BlockFinished(7); // not found: handed to the bounded 30 minute background retry
            _time.Advance(TimeSpan.FromMinutes(29));
            _monitor.Heartbeat();
            _monitor.BlockStarted(7); // recovered: its processing starts a fresh clock
            _time.Advance(TimeSpan.FromMinutes(29));
            _monitor.Heartbeat();
            Assert.That(_monitor.DescribeStall(Threshold), Is.Null, "58 minutes since the first attempt, but neither phase hung");

            _time.Advance(TimeSpan.FromMinutes(2));
            _monitor.Heartbeat();
            Assert.That(_monitor.DescribeStall(Threshold), Does.Contain("block 7"), "the recovered block's processing hung");
        }

        [Test]
        public void ZeroThreshold_DisablesTheCheck()
        {
            _monitor.Heartbeat();
            _monitor.BlockStarted(7);
            _time.Advance(TimeSpan.FromDays(5));
            Assert.That(_monitor.DescribeStall(TimeSpan.Zero), Is.Null);
        }

        [Test]
        public async Task HealthCheck_IsUnhealthyOnAStall_SoTheLivenessProbeRestartsThePod()
        {
            var config = new AppConfiguration();
            Assert.That(config.BlockProcessing.StallRestartMinutes, Is.EqualTo(30), "enabled by default");
            var check = new IndexerStallHealthCheck(_monitor, Options.Create(config), NullLogger<IndexerStallHealthCheck>.Instance);

            _monitor.Heartbeat();
            _monitor.BlockStarted(66736698);
            Assert.That((await check.CheckHealthAsync(new HealthCheckContext())).Status, Is.EqualTo(HealthStatus.Healthy));

            _time.Advance(TimeSpan.FromMinutes(31));
            _monitor.Heartbeat();
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            // Unhealthy, not Degraded: /health maps Degraded to 200 and the probe would never restart the pod
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That(result.Description, Does.Contain("66736698"));

            config.BlockProcessing.StallRestartMinutes = 0;
            Assert.That((await check.CheckHealthAsync(new HealthCheckContext())).Status, Is.EqualTo(HealthStatus.Healthy), "0 switches the restart off");
        }
    }
}
