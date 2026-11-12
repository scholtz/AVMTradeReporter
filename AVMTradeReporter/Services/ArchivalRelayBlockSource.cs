using Algorand.Algod.Model;
using Algorand.Gossip;
using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Fetches blocks from the network's archival relay nodes over the gossip (node to node) protocol - the same
    /// <c>/v1/{genesis}/block/{round}</c> endpoint a node uses to catch up - instead of an algod REST API.
    /// </summary>
    /// <remarks>
    /// Why: an algod API is the wrong source for a backlog. Our own node keeps about 1 000 rounds, and a public algod
    /// service is rate limited and has a quota (2026-11-12: block fetches from the cluster failed for hours while
    /// production was 60 000 blocks behind). Archival relays exist to serve old blocks to nodes that sync, keep the whole
    /// chain, and there are many of them (mainnet 13, testnet 4, Voi 2 at the time of writing, discovered through the
    /// <c>_archive._tcp</c> DNS SRV record), so the load is spread instead of aimed at one service.
    /// <para>
    /// Every relay is used: a block goes to the relay with the shortest expected wait (its measured latency times the
    /// requests it already has in flight), so fast relays get more and no relay gets more than
    /// <see cref="ArchivalRelayConfiguration.MaxInFlightPerRelay"/> at once. A relay that fails rests for a growing time
    /// and the block is asked from the next one. A block of another chain, or of another round, is never accepted.
    /// </para>
    /// </remarks>
    public sealed class ArchivalRelayBlockSource
    {
        /// <summary>Fetches one block from one relay; null when the relay does not have it.</summary>
        public delegate Task<CertifiedBlock?> RelayFetch(string host, ulong round, CancellationToken cancellationToken);

        /// <summary>The archival relays of the network and the genesis id they serve.</summary>
        public delegate (string GenesisId, IReadOnlyList<string> Hosts) RelayDiscovery();

        private sealed class Relay
        {
            public required string Host { get; init; }
            public double LatencyMs = InitialLatencyMs; // exponentially weighted; guarded by the source's lock
            public int InFlight;
            public int ConsecutiveFailures;
            public DateTimeOffset RestUntil = DateTimeOffset.MinValue;
            public long Served;
            public long Failed;
        }

        /// <summary>What a relay nobody has measured yet is assumed to need: it is tried soon, not first.</summary>
        private const double InitialLatencyMs = 150;
        private static readonly TimeSpan FirstRest = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LongestRest = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DiscoveryRetry = TimeSpan.FromMinutes(1);

        private readonly ArchivalRelayConfiguration _config;
        private readonly string _expectedGenesisId;
        private readonly ILogger<ArchivalRelayBlockSource> _logger;
        private readonly TimeProvider _time;
        private readonly RelayFetch _fetch;
        private readonly RelayDiscovery _discover;
        private readonly object _lock = new();
        private Task? _discovery; // the discovery in progress, shared by every caller
        private List<Relay> _relays = new();
        private DateTimeOffset _discoveredAt = DateTimeOffset.MinValue;
        private DateTimeOffset _nextDiscoveryAt = DateTimeOffset.MinValue;
        private bool _wrongChain;

        public ArchivalRelayBlockSource(IOptions<AppConfiguration> options, ILogger<ArchivalRelayBlockSource> logger)
            : this(options.Value.ArchivalRelays, options.Value.GenesisId, logger, null, null, DnsDiscovery(options.Value))
        {
        }

        public ArchivalRelayBlockSource(
            ArchivalRelayConfiguration config,
            string expectedGenesisId,
            ILogger<ArchivalRelayBlockSource> logger,
            TimeProvider? timeProvider,
            RelayFetch? fetch,
            RelayDiscovery discover)
        {
            _config = config;
            _expectedGenesisId = expectedGenesisId;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _discover = discover;
            _fetch = fetch ?? new GossipFetcher(expectedGenesisId).FetchAsync;
        }

        public bool Enabled => _config.Enabled && !_wrongChain;

        /// <summary>The relays in use with what is known about them (diagnostics).</summary>
        public IReadOnlyList<(string Host, double LatencyMs, long Served, long Failed, bool Resting)> Snapshot()
        {
            var now = _time.GetUtcNow();
            lock (_lock) return _relays.Select(r => (r.Host, r.LatencyMs, r.Served, r.Failed, r.RestUntil > now)).ToList();
        }

        /// <summary>
        /// The block from an archival relay, or null when none of the relays tried could return it (the caller then
        /// falls back to algod). Never throws except for the cancellation of <paramref name="cancellationToken"/>.
        /// </summary>
        public async Task<CertifiedBlock?> TryFetchBlockAsync(ulong round, CancellationToken cancellationToken)
        {
            if (!Enabled) return null;
            await EnsureDiscoveredAsync(cancellationToken);

            var tried = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 0; attempt < Math.Max(1, _config.MaxAttempts); attempt++)
            {
                var relay = await AcquireRelayAsync(tried, cancellationToken);
                if (relay == null) return null; // no relay left that is not resting or already tried
                tried.Add(relay.Host);

                var started = _time.GetUtcNow();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.RequestTimeoutSeconds)));
                    var block = await _fetch(relay.Host, round, timeout.Token).WaitAsync(timeout.Token);
                    if (block?.Block == null)
                    {
                        // the relay does not have the block (not there yet, or not as archival as advertised): not a fault
                        Release(relay, _time.GetUtcNow() - started, failed: false, served: false);
                        continue;
                    }
                    if (block.Block.Round != round || !string.Equals(block.Block.GenesisId, _expectedGenesisId, StringComparison.Ordinal))
                    {
                        _logger.LogError("Archival relay {relay} answered block {round} with round {actualRound} of {genesis} (expected {expectedGenesis}) - rejected", relay.Host, round, block.Block.Round, block.Block.GenesisId, _expectedGenesisId);
                        Release(relay, _time.GetUtcNow() - started, failed: true, served: false);
                        continue;
                    }
                    Release(relay, _time.GetUtcNow() - started, failed: false, served: true);
                    return block;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Release(relay, _time.GetUtcNow() - started, failed: false, served: false);
                    throw;
                }
                catch (Exception ex)
                {
                    // a warning without the stack trace: a resting relay is routine, the next one is asked
                    _logger.LogWarning("Archival relay {relay} failed to return block {round}: {error}", relay.Host, round, ex.GetType().Name + ": " + ex.Message);
                    Release(relay, _time.GetUtcNow() - started, failed: true, served: false);
                }
            }
            return null;
        }

        /// <summary>
        /// The usable relay with the shortest expected wait. When every usable relay is at its in-flight limit it waits
        /// for one to finish; null when there is no usable relay at all.
        /// </summary>
        private async Task<Relay?> AcquireRelayAsync(HashSet<string> tried, CancellationToken cancellationToken)
        {
            while (true)
            {
                var now = _time.GetUtcNow();
                var anyBusy = false;
                lock (_lock)
                {
                    Relay? best = null;
                    var bestWait = double.MaxValue;
                    foreach (var relay in _relays)
                    {
                        if (relay.RestUntil > now || tried.Contains(relay.Host)) continue;
                        if (relay.InFlight >= Math.Max(1, _config.MaxInFlightPerRelay))
                        {
                            anyBusy = true;
                            continue;
                        }
                        var wait = relay.LatencyMs * (relay.InFlight + 1);
                        if (wait < bestWait)
                        {
                            bestWait = wait;
                            best = relay;
                        }
                    }
                    if (best != null)
                    {
                        best.InFlight++;
                        return best;
                    }
                }
                if (!anyBusy) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(5), _time, cancellationToken);
            }
        }

        private void Release(Relay relay, TimeSpan elapsed, bool failed, bool served)
        {
            lock (_lock)
            {
                relay.InFlight--;
                if (failed)
                {
                    relay.Failed++;
                    relay.ConsecutiveFailures++;
                    var rest = TimeSpan.FromTicks(Math.Min(LongestRest.Ticks, FirstRest.Ticks << Math.Min(relay.ConsecutiveFailures - 1, 10)));
                    relay.RestUntil = _time.GetUtcNow() + rest;
                    return;
                }
                if (!served) return;
                relay.Served++;
                relay.ConsecutiveFailures = 0;
                relay.LatencyMs = relay.LatencyMs * 0.8 + elapsed.TotalMilliseconds * 0.2;
            }
        }

        /// <summary>
        /// Discovers the relays when there are none yet or the list is due for a refresh. One discovery runs at a time and
        /// is shared: callers that have no relay to use wait for it (a caller that returned early would report "no relay"
        /// for its block), callers that still have the previous list go on with it.
        /// </summary>
        private async Task EnsureDiscoveredAsync(CancellationToken cancellationToken)
        {
            Task? discovery;
            bool mustWait;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                var due = _relays.Count == 0 || now - _discoveredAt >= TimeSpan.FromMinutes(Math.Max(1, _config.RediscoverMinutes));
                if (due && _discovery == null && now >= _nextDiscoveryAt)
                {
                    _nextDiscoveryAt = now + DiscoveryRetry;
                    _discovery = DiscoverAsync();
                }
                discovery = _discovery;
                mustWait = _relays.Count == 0;
            }
            if (discovery != null && mustWait) await discovery.WaitAsync(cancellationToken);
        }

        private async Task DiscoverAsync()
        {
            try
            {
                // the DNS lookup is synchronous in the SDK: off the caller's thread
                var (genesisId, hosts) = await Task.Run(() => _discover());
                if (!string.Equals(genesisId, _expectedGenesisId, StringComparison.Ordinal))
                {
                    _wrongChain = true;
                    _logger.LogError("Archival relays are disabled: the configured relay network serves {relayGenesis}, this indexer follows {genesis} (check AppConfiguration.GossipDiscovery.Network)", genesisId, _expectedGenesisId);
                    return;
                }
                lock (_lock)
                {
                    var known = _relays.ToDictionary(r => r.Host, StringComparer.Ordinal);
                    _relays = hosts.Distinct(StringComparer.Ordinal).Select(h => known.TryGetValue(h, out var relay) ? relay : new Relay { Host = h }).ToList();
                    _discoveredAt = _time.GetUtcNow();
                }
                _logger.LogInformation("Using {count} archival relays for block fetching: {relays}", hosts.Count, string.Join(", ", hosts));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not discover the archival relays; trying again in {seconds} seconds", DiscoveryRetry.TotalSeconds);
            }
            finally
            {
                lock (_lock) _discovery = null;
            }
        }

        private static RelayDiscovery DnsDiscovery(AppConfiguration config)
        {
            return () =>
            {
                if (config.ArchivalRelays.Hosts is { Count: > 0 } configured) return (config.GenesisId, configured);
                var discovered = new GossipHttpConfiguration(GossipNodePurpose.Archival, config.GossipDiscovery.Network, "http");
                return (discovered.GenesisId, discovered.Hosts);
            };
        }

        /// <summary>The SDK's gossip HTTP client, one per relay (a client asks the hosts of its configuration).</summary>
        private sealed class GossipFetcher
        {
            private readonly string _genesisId;
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GossipHttpClient> _clients = new(StringComparer.Ordinal);

            public GossipFetcher(string genesisId)
            {
                _genesisId = genesisId;
            }

            public Task<CertifiedBlock?> FetchAsync(string host, ulong round, CancellationToken cancellationToken)
            {
                var client = _clients.GetOrAdd(host, h => new GossipHttpClient(new GossipHttpConfiguration { GenesisId = _genesisId, Hosts = new[] { h } }));
                return client.FetchBlockAsync(round, 1)!;
            }
        }
    }
}
