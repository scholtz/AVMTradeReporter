using Algorand.Algod.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// A catch-up gets its blocks from the archival relay nodes over the gossip protocol, from all of them, and never
    /// accepts a block of another chain or round. See CLAUDE.md, "Sync speed", rule 6.
    /// </summary>
    [TestFixture]
    public class ArchivalRelayBlockSourceTests
    {
        private const string Genesis = "mainnet-v1.0";

        private sealed class FakeTime : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-12T20:00:00Z");
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private static CertifiedBlock Block(ulong round, string genesis = Genesis) => new() { Block = new Block { Round = round, GenesisId = genesis } };

        private static ArchivalRelayBlockSource Source(
            ArchivalRelayBlockSource.RelayFetch fetch,
            string[]? hosts = null,
            string relayGenesis = Genesis,
            ArchivalRelayConfiguration? config = null,
            TimeProvider? time = null)
        {
            hosts ??= new[] { "http://a", "http://b", "http://c" };
            return new ArchivalRelayBlockSource(config ?? new ArchivalRelayConfiguration(), Genesis, NullLogger<ArchivalRelayBlockSource>.Instance, time, fetch, () => (relayGenesis, hosts));
        }

        [Test]
        public async Task Block_ComesFromARelay()
        {
            var source = Source((host, round, _) => Task.FromResult<CertifiedBlock?>(Block(round)));
            var block = await source.TryFetchBlockAsync(66736698, default);
            Assert.That(block!.Block!.Round, Is.EqualTo(66736698UL));
        }

        [Test]
        public async Task EveryRelayIsUsed_AndFasterRelaysServeMore()
        {
            var served = new ConcurrentDictionary<string, int>();
            var source = Source(async (host, round, _) =>
            {
                await Task.Delay(host == "http://a" ? 5 : host == "http://b" ? 25 : 60);
                served.AddOrUpdate(host, 1, (_, n) => n + 1);
                return Block(round);
            });

            ulong next = 1000;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                for (var i = 0; i < 30; i++) Assert.That(await source.TryFetchBlockAsync(Interlocked.Increment(ref next), default), Is.Not.Null);
            }));

            Assert.That(served.Keys, Is.EquivalentTo(new[] { "http://a", "http://b", "http://c" }), "the load is spread over all relays");
            Assert.That(served["http://a"], Is.GreaterThan(served["http://c"]), "the fastest relay serves more than the slowest");
        }

        [Test]
        public async Task OneRelay_NeverServesMoreThanItsLimitAtOnce()
        {
            var config = new ArchivalRelayConfiguration { MaxInFlightPerRelay = 2 };
            var inFlight = new ConcurrentDictionary<string, int>();
            var worst = 0;
            var source = Source(async (host, round, _) =>
            {
                var now = inFlight.AddOrUpdate(host, 1, (_, n) => n + 1);
                InterlockedMax(ref worst, now);
                await Task.Delay(20);
                inFlight.AddOrUpdate(host, 0, (_, n) => n - 1);
                return Block(round);
            }, config: config);

            await Task.WhenAll(Enumerable.Range(0, 40).Select(i => source.TryFetchBlockAsync((ulong)(1000 + i), default)));
            Assert.That(worst, Is.LessThanOrEqualTo(2), "a catch-up does not lean on a single relay");
        }

        [Test]
        public async Task FailingRelay_Rests_AndTheBlockComesFromTheNextOne()
        {
            var time = new FakeTime();
            var asked = new List<string>();
            var source = Source(async (host, round, _) =>
            {
                lock (asked) asked.Add(host);
                if (host == "http://a") throw new HttpRequestException("connection refused");
                await Task.Delay(20);
                return Block(round);
            }, hosts: new[] { "http://a", "http://b" }, time: time);

            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Not.Null, "served by the other relay");
            Assert.That(asked, Does.Contain("http://a"));

            asked.Clear();
            for (ulong round = 1001; round < 1020; round++) Assert.That(await source.TryFetchBlockAsync(round, default), Is.Not.Null);
            Assert.That(asked, Has.None.EqualTo("http://a"), "the failed relay rests");

            // more blocks at once than the healthy relay may serve: the rested relay gets its chance
            time.Advance(TimeSpan.FromMinutes(6));
            asked.Clear();
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => source.TryFetchBlockAsync((ulong)(1020 + i), default)));
            Assert.That(asked, Does.Contain("http://a"), "and is tried again after its rest");

            // even while it was resting, the 12 blocks at once did not go to it
            asked.Clear();
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => source.TryFetchBlockAsync((ulong)(1040 + i), default)));
            Assert.That(asked, Has.None.EqualTo("http://a"), "it failed again, so it rests again");
        }

        [Test]
        public async Task BlockThatNoRelayHasYet_IsAnAnswer_NotAFailure()
        {
            // the round does not exist yet (404 from every relay): nobody is punished, the caller simply gets no block
            var asked = new List<string>();
            var source = Source((host, round, _) =>
            {
                lock (asked) asked.Add(host);
                return Task.FromResult<CertifiedBlock?>(null);
            }, hosts: new[] { "http://a", "http://b" });

            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Null);
            Assert.That(asked, Is.EquivalentTo(new[] { "http://a", "http://b" }), "the next relay is asked");
            Assert.That(source.Snapshot().Select(r => r.Resting), Has.All.False, "\"I do not have it\" is an answer");
        }

        [Test]
        public async Task RelayThatLacksABlockAnotherRelayHas_IsNotAskedFirstForTheNextBlocks()
        {
            // listed as archival, but it is not (or it is far behind): every old block would lose an attempt on it
            var asked = new List<string>();
            var source = Source((host, round, _) =>
            {
                lock (asked) asked.Add(host);
                return Task.FromResult(host == "http://a" ? null : Block(round));
            }, hosts: new[] { "http://a", "http://b" });

            for (ulong round = 1000; round < 1030; round++) Assert.That(await source.TryFetchBlockAsync(round, default), Is.Not.Null);
            Assert.That(asked.Count(h => h == "http://a"), Is.LessThanOrEqualTo(1));
        }

        [Test]
        public async Task BusyRelay_RestsForAsLongAsItAsks()
        {
            var time = new FakeTime();
            var asked = new List<string>();
            var source = Source((host, round, _) =>
            {
                lock (asked) asked.Add(host);
                if (host == "http://a") throw new RelayBusyException(System.Net.HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(60));
                return Task.FromResult<CertifiedBlock?>(Block(round));
            }, hosts: new[] { "http://a", "http://b" }, config: new ArchivalRelayConfiguration { MaxInFlightPerRelay = 1 }, time: time);

            // "a" is unmeasured and "b" is instant, so "a" is only asked while "b" is busy: ask two blocks at once
            await Task.WhenAll(source.TryFetchBlockAsync(1000, default), source.TryFetchBlockAsync(1001, default));
            Assert.That(source.Snapshot().Single(r => r.Host == "http://a").Resting || !asked.Contains("http://a"), Is.True);

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.That(source.Snapshot().Single(r => r.Host == "http://a").Resting, Is.EqualTo(asked.Contains("http://a")), "still resting 30 s into the 60 s it asked for");
            time.Advance(TimeSpan.FromSeconds(31));
            Assert.That(source.Snapshot().Single(r => r.Host == "http://a").Resting, Is.False);
        }

        [Test]
        public async Task HungRelay_IsAbandonedAfterTheTimeout_AndItsRequestIsCancelled()
        {
            var cancelled = false;
            var source = Source(async (host, round, token) =>
            {
                if (host != "http://a") return Block(round);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return null;
            }, hosts: new[] { "http://a" }, config: new ArchivalRelayConfiguration { RequestTimeoutSeconds = 1 });

            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Null, "the caller falls back to algod");
            Assert.That(() => Volatile.Read(ref cancelled), Is.True.After(2000, 10), "the request itself is aborted, not only abandoned");
            Assert.That(source.Snapshot().Single().Resting, Is.True);
        }

        [Test]
        public async Task EveryRelayBusy_FallsBackAfterTheWait_InsteadOfQueueingForEver()
        {
            var release = new TaskCompletionSource();
            var source = Source(async (host, round, _) => { await release.Task; return Block(round); },
                hosts: new[] { "http://a" }, config: new ArchivalRelayConfiguration { MaxInFlightPerRelay = 1, RequestTimeoutSeconds = 1 });

            var first = source.TryFetchBlockAsync(1000, default);
            var second = await source.TryFetchBlockAsync(1001, default);
            Assert.That(second, Is.Null, "no free relay within the wait: algod is asked");
            release.SetResult();
            await first;
        }

        [Test]
        public async Task BlockOfAnotherRoundOrChain_IsNeverAccepted()
        {
            var source = Source((host, round, _) => Task.FromResult<CertifiedBlock?>(host switch
            {
                "http://a" => Block(round + 1),               // another round
                "http://b" => Block(round, "testnet-v1.0"),    // another chain
                _ => Block(round),
            }));

            var block = await source.TryFetchBlockAsync(1000, default);
            Assert.That(block!.Block!.Round, Is.EqualTo(1000UL));
            Assert.That(block.Block.GenesisId, Is.EqualTo(Genesis));

            var onlyWrong = Source((host, round, _) => Task.FromResult<CertifiedBlock?>(Block(round, "testnet-v1.0")));
            Assert.That(await onlyWrong.TryFetchBlockAsync(1000, default), Is.Null, "no relay returned a block of this chain: the caller falls back to algod");
        }

        [Test]
        public async Task RelayNetworkOfAnotherChain_DisablesTheSource()
        {
            // AppConfiguration.GossipDiscovery.Network left at its default (mainnet) on a testnet deployment
            var fetches = 0;
            var source = Source((host, round, _) => { fetches++; return Task.FromResult<CertifiedBlock?>(Block(round)); }, relayGenesis: "testnet-v1.0");

            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Null);
            Assert.That(source.Enabled, Is.False);
            Assert.That(fetches, Is.Zero, "not a single request goes to a relay of the wrong chain");
        }

        [Test]
        public async Task AllRelaysFail_ReturnsNull_SoTheCallerFallsBackToAlgod()
        {
            var source = Source((_, _, _) => throw new HttpRequestException("down"));
            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Null);
        }

        [Test]
        public async Task Disabled_AsksNoRelay()
        {
            var fetches = 0;
            var source = Source((host, round, _) => { fetches++; return Task.FromResult<CertifiedBlock?>(Block(round)); }, config: new ArchivalRelayConfiguration { Enabled = false });
            Assert.That(await source.TryFetchBlockAsync(1000, default), Is.Null);
            Assert.That(fetches, Is.Zero);
        }

        [Test]
        public async Task FirstBlocksOfACatchUp_AllWaitForTheDiscovery_NoneIsReportedMissing()
        {
            // 32 block tasks ask at the same moment, before the relays are known (the DNS lookup takes a while)
            var source = new ArchivalRelayBlockSource(new ArchivalRelayConfiguration(), Genesis, NullLogger<ArchivalRelayBlockSource>.Instance, null,
                (host, round, _) => Task.FromResult<CertifiedBlock?>(Block(round)),
                () => { Thread.Sleep(150); return (Genesis, new[] { "http://a", "http://b" }); });

            var blocks = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => source.TryFetchBlockAsync((ulong)(1000 + i), default)));
            Assert.That(blocks, Has.None.Null);
        }

        [Test]
        public void Defaults_UseTheRelays()
        {
            var config = new AppConfiguration();
            Assert.That(config.ArchivalRelays.Enabled, Is.True);
            Assert.That(config.ArchivalRelays.Hosts, Is.Empty, "discovered through DNS");
            Assert.That(config.ArchivalRelays.MaxInFlightPerRelay, Is.InRange(1, 8));
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
