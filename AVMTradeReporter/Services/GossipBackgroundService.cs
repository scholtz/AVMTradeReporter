
using Algorand.Algod.Model.Transactions;
using Algorand.Gossip;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    public class GossipRelayStatus
    {
        public string Host { get; set; } = string.Empty;
        public DateTime ConnectedAtUtc { get; set; }
        public long MessageCount { get; set; }
        public long WinCount { get; set; }
        public DateTime? LastMessageUtc { get; set; }
    }

    public class GossipBackgroundService : BackgroundService, ITradeService, ILiquidityService
    {
        private readonly ILogger<GossipBackgroundService> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptions<AppConfiguration> _appConfig;
        private readonly TradeRepository _tradeRepository;
        private readonly LiquidityRepository _liquidityRepository;
        private readonly MemoryCache _tx_cache = new MemoryCache(new MemoryCacheOptions());
        private readonly MemoryCache _tx_group = new MemoryCache(new MemoryCacheOptions());
        private readonly TransactionProcessor _transactionProcessor;
        public GossipBackgroundService(
            ILoggerFactory loggerFactory,
            IOptions<AppConfiguration> appConfig,
            TradeRepository tradeRepository,
            LiquidityRepository liquidityRepository,
            TransactionProcessor transactionProcessor
            )
        {
            _logger = loggerFactory.CreateLogger<GossipBackgroundService>();
            _loggerFactory = loggerFactory;
            _tradeRepository = tradeRepository;
            _liquidityRepository = liquidityRepository;
            _transactionProcessor = transactionProcessor;
            _appConfig = appConfig;
        }

        ConcurrentDictionary<string, Trade> _trades = new ConcurrentDictionary<string, Trade>();
        ConcurrentDictionary<string, Liquidity> _liquidityUpdates = new ConcurrentDictionary<string, Liquidity>();
        public Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            _trades[trade.TxId] = trade;
            return Task.CompletedTask;
        }

        public Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            _liquidityUpdates[liquidityUpdate.TxId] = liquidityUpdate;
            return Task.CompletedTask;
        }

        private async Task FinalizeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _tradeRepository.StoreTradesAsync(_trades.Values.ToArray(), cancellationToken);
                if (result)
                {
                    _trades.Clear();
                }
                result = await _liquidityRepository.StoreLiquidityUpdatesAsync(_liquidityUpdates.Values.ToArray(), cancellationToken);
                if (result)
                {
                    _liquidityUpdates.Clear();
                }
                await Task.CompletedTask; // Placeholder for actual work
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }
        private class RelayCandidate
        {
            public string Host { get; set; } = string.Empty;
            public GossipWebsocketClient? Client { get; set; }
            public DateTime ConnectedAtUtc { get; set; }
            public long WinCount;
            public long MessageCount;
            public DateTime LastMessageUtc;
        }

        ConcurrentDictionary<string, RelayCandidate> _relayCandidates = new();

        public IEnumerable<GossipRelayStatus> GetRelayStatus()
        {
            return _relayCandidates.Values.Select(c => new GossipRelayStatus
            {
                Host = c.Host,
                ConnectedAtUtc = c.ConnectedAtUtc,
                MessageCount = c.MessageCount,
                WinCount = c.WinCount,
                LastMessageUtc = c.LastMessageUtc == default ? (DateTime?)null : c.LastMessageUtc,
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var staticConfigs = _appConfig.Value.GossipWebsocketClientConfigurations;
            var staticHosts = staticConfigs?.Where(c => !string.IsNullOrWhiteSpace(c.Host)).Select(c => c.Host).Distinct().ToList() ?? new List<string>();
            var hasConfiguredHost = staticHosts.Count > 0;
            var discoveryConfig = _appConfig.Value.GossipDiscovery;

            List<string> relayHosts;
            if (hasConfiguredHost)
            {
                relayHosts = staticHosts;
                _logger.LogInformation($"Using {relayHosts.Count} statically configured gossip relay(s).");
            }
            else
            {
                // No static relay configured: discover all known relays via DNS SRV.
                try
                {
                    var gossipHttpConfig = new GossipHttpConfiguration(GossipNodePurpose.Relay, GossipNetwork.AlgorandMainNet, "ws");
                    relayHosts = gossipHttpConfig.Hosts
                        .Select(h => $"{h}/v1/{gossipHttpConfig.GenesisId}/gossip")
                        .Distinct()
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve gossip relay hosts via DNS SRV");
                    relayHosts = new List<string>();
                }

                _logger.LogInformation($"Discovered {relayHosts.Count} gossip relays. Connecting to all of them to determine the fastest {discoveryConfig.MaxActiveRelayConnections}.");
            }

            foreach (var host in relayHosts)
            {
                await ConnectRelayAsync(host);
            }

            var startedUtc = DateTime.UtcNow;
            // A static relay list is already the exact set we want connected, so there's nothing to prune down to.
            var warmedUp = hasConfiguredHost;
            var lastEvaluationUtc = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                if (!warmedUp)
                {
                    if ((DateTime.UtcNow - startedUtc).TotalSeconds < discoveryConfig.WarmUpSeconds)
                    {
                        continue;
                    }
                    warmedUp = true;
                    PruneToFastestRelays(discoveryConfig.MaxActiveRelayConnections);
                    lastEvaluationUtc = DateTime.UtcNow;
                    continue;
                }

                if ((DateTime.UtcNow - lastEvaluationUtc).TotalSeconds < discoveryConfig.ReEvaluationIntervalSeconds)
                {
                    continue;
                }
                lastEvaluationUtc = DateTime.UtcNow;
                // Reconnects any relay (static or discovered) that has gone silent for too long, so a dead
                // relay never permanently starves this service of gossip traffic.
                await ReplaceStaleRelaysAsync(relayHosts, discoveryConfig);
            }
        }

        private async Task ConnectRelayAsync(string host)
        {
            if (_relayCandidates.ContainsKey(host)) return;

            var candidate = new RelayCandidate
            {
                Host = host,
                ConnectedAtUtc = DateTime.UtcNow,
            };

            if (!_relayCandidates.TryAdd(host, candidate))
            {
                return;
            }

            var clientConfig = new GossipWebsocketClientConfiguration { Host = host };
            var client = new GossipWebsocketClient(_loggerFactory.CreateLogger<GossipWebsocketClient>(), clientConfig);
            candidate.Client = client;
            client.TransactionReceivedEvent += (sender, txs) => OnRelayTransactionReceived(candidate, txs);

            try
            {
                await client.Start();
                _logger.LogInformation($"Connected to gossip relay {host}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to connect to gossip relay {host}");
                _relayCandidates.TryRemove(host, out _);
            }
        }

        private void DisconnectRelay(RelayCandidate candidate)
        {
            _relayCandidates.TryRemove(candidate.Host, out _);
            try
            {
                candidate.Client?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error disposing gossip relay client {candidate.Host}");
            }
        }

        private void PruneToFastestRelays(int maxActive)
        {
            var ranked = _relayCandidates.Values
                .OrderByDescending(c => c.WinCount)
                .ThenByDescending(c => c.MessageCount)
                .ToList();

            var toKeep = ranked.Take(maxActive).Select(c => c.Host).ToHashSet();

            foreach (var candidate in ranked)
            {
                if (toKeep.Contains(candidate.Host)) continue;
                DisconnectRelay(candidate);
            }

            _logger.LogInformation($"Pruned gossip relays down to the fastest {toKeep.Count}: {string.Join(", ", toKeep)}");
        }

        private async Task ReplaceStaleRelaysAsync(List<string> allRelayHosts, GossipDiscoveryConfiguration config)
        {
            var now = DateTime.UtcNow;
            var staleThreshold = TimeSpan.FromSeconds(config.StaleRelaySeconds);

            var stale = _relayCandidates.Values
                .Where(c => now - (c.LastMessageUtc == default ? c.ConnectedAtUtc : c.LastMessageUtc) > staleThreshold)
                .ToList();

            foreach (var candidate in stale)
            {
                _logger.LogInformation($"Gossip relay {candidate.Host} has been silent for over {config.StaleRelaySeconds}s, replacing it.");
                DisconnectRelay(candidate);
            }

            var connectedHosts = _relayCandidates.Keys.ToHashSet();
            var deficit = config.MaxActiveRelayConnections - connectedHosts.Count;
            if (deficit <= 0) return;

            var replacements = allRelayHosts.Where(h => !connectedHosts.Contains(h)).Take(deficit);
            foreach (var host in replacements)
            {
                await ConnectRelayAsync(host);
            }
        }

        private async Task OnRelayTransactionReceived(RelayCandidate candidate, IEnumerable<SignedTransaction> txs)
        {
            var tx = txs.FirstOrDefault();
            if (tx != null)
            {
                candidate.MessageCount++;
                candidate.LastMessageUtc = DateTime.UtcNow;
                if (!_tx_cache.TryGetValue(tx.Tx.TxID(), out _))
                {
                    candidate.WinCount++;
                }
            }

            await Client1_TransactionReceivedEvent(candidate, txs);
        }

        private async Task Client1_TransactionReceivedEvent(object sender, IEnumerable<SignedTransaction> txs)
        {
            var tx = txs.FirstOrDefault();
            if (tx == null) return;

            var txId = tx.Tx.TxID();
            if (_tx_cache.TryGetValue(txId, out _))
            {
                // Transaction already processed
                return;
            }
            _tx_cache.Set(txId, tx, TimeSpan.FromMinutes(10));
            var cancellationTokenSource = new CancellationTokenSource();
            if (tx.Tx.Group != null && tx.Tx.Group.Bytes.Length > 0)
            {
                List<SignedTransaction> txsGroup = txs.ToList();
                _tx_group.Set(tx.Tx.Group.ToString(), txsGroup, TimeSpan.FromMinutes(10));

                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx1 = null;
                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx2 = null;
                if (txsGroup != null)
                {
                    ulong index = 0;
                    foreach (var currTx in txsGroup)
                    {
                        index++;
                        try
                        {
                            //currTx.Tx.FillInParamsFromBlockHeader(block.Block);
                            var currTxId = currTx.Tx.TxID();
                            await _transactionProcessor.ProcessTransaction(currTx, prevTx1, prevTx2, null, currTx.Tx.Group, currTxId, currTx.Tx.Sender, TxState.TxPool, this, this, cancellationTokenSource.Token);
                        }
                        catch (Exception exc)
                        {
                            _logger.LogInformation("Error processing transaction from gossip {group}: {error}", tx.Tx.Group.ToString(), exc.Message);
                        }
                        prevTx2 = prevTx1;
                        prevTx1 = currTx;
                    }
                }

            }
            await FinalizeAsync(cancellationTokenSource.Token);
        }

    }
}
