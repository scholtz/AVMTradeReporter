using AVMTradeReporter.Models.Data;
using StackExchange.Redis;
using System.Text.Json;
using System.Collections.Concurrent;
using System.IO; // added for optional seed file
using System.Linq; // for Take()
using Microsoft.Extensions.Logging;

namespace AVMTradeReporter.Subscriber;

class Program
{
    static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        ILogger logger = loggerFactory.CreateLogger<Program>();

        logger.LogDebug("AVM Trade Reporter - Redis PubSub Subscriber");
        logger.LogDebug("=============================================");
        logger.LogDebug("");

        // Configuration - can be overridden via command line arguments or environment variables
        var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
        var redisDbIdStr = Environment.GetEnvironmentVariable("REDIS_DB") ?? Environment.GetEnvironmentVariable("REDIS_DATABASE_ID") ?? "0";
        int redisDbId = int.TryParse(redisDbIdStr, out var parsedDb) ? parsedDb : 0;
        var poolChannel = Environment.GetEnvironmentVariable("POOL_CHANNEL") ?? "avmtrade:pool:updates";
        var aggregatedPoolChannel = Environment.GetEnvironmentVariable("AGGREGATED_POOL_CHANNEL") ?? "avmtrade:aggregatedpool:updates";
        var poolKeyPrefix = Environment.GetEnvironmentVariable("POOL_KEY_PREFIX") ?? "avmtrade:pools:"; // matches AppConfiguration.Redis.KeyPrefix
        var aggregatedPoolKeyPrefix = Environment.GetEnvironmentVariable("AGGREGATED_POOL_KEY_PREFIX") ?? "avmtrade:aggregatedpools:"; // assumed prefix for persisted aggregated pools if available
        var seedPoolsFile = Environment.GetEnvironmentVariable("SEED_POOLS_FILE") ?? "AVMTradeReporterTests/Data/pools-algo-usdc.json"; // optional
        var seedAggregatedFile = Environment.GetEnvironmentVariable("SEED_AGGREGATED_POOLS_FILE") ?? string.Empty; // optional JSON array of AggregatedPool
        var enableSeeding = (Environment.GetEnvironmentVariable("ENABLE_REDIS_SEEDING") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        logger.LogDebug($"Connecting to Redis at: {redisConnectionString}");
        logger.LogDebug($"Redis DB Id: {redisDbId}");
        logger.LogDebug($"Pool updates channel: {poolChannel}");
        logger.LogDebug($"Aggregated pool updates channel: {aggregatedPoolChannel}");
        logger.LogDebug($"Pool key prefix: {poolKeyPrefix}");
        logger.LogDebug($"Aggregated pool key prefix: {aggregatedPoolKeyPrefix}");
        logger.LogDebug($"Enable seeding: {enableSeeding}");
        logger.LogDebug("");

        try
        {
            // Connect to Redis (add allowAdmin for key scan if not provided)
            if (!redisConnectionString.Contains("allowAdmin", StringComparison.OrdinalIgnoreCase))
            {
                redisConnectionString = redisConnectionString.Contains(",") ? redisConnectionString + ",allowAdmin=true" : redisConnectionString + ",allowAdmin=true";
            }
            var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var subscriber = redis.GetSubscriber();
            var db = redis.GetDatabase(redisDbId);

            logger.LogDebug("Connected to Redis successfully!");
            logger.LogDebug("");

            // Preload existing pools from Redis (if keys exist)
            var preloadPools = new List<Pool>();
            var preloadAggregatedPools = new List<AggregatedPool>();

            try
            {
                var endpoints = redis.GetEndPoints();
                if (endpoints.Length > 0)
                {
                    var server = redis.GetServer(endpoints[0]);
                    // Load Pools using index set if available
                    logger.LogDebug("Preloading pools from Redis...");
                    int poolCounter = 0;
                    var poolIndexKey = poolKeyPrefix + "index";
                    
                    logger.LogDebug($"Checking for pool index at key: {poolIndexKey}");
                    
                    if (await db.KeyExistsAsync(poolIndexKey))
                    {
                        logger.LogDebug($"Pool index found at {poolIndexKey}");
                        var members = await db.SetMembersAsync(poolIndexKey);
                        logger.LogDebug($"Pool index contains {members.Length} members");
                        
                        foreach (var member in members)
                        {
                            try
                            {
                                var redisKey = poolKeyPrefix + member.ToString();
                                var val = await db.StringGetAsync(redisKey);
                                if (!val.IsNullOrEmpty)
                                {
                                    var pool = JsonSerializer.Deserialize<Pool>((string)val!);
                                    if (pool != null)
                                    {
                                        preloadPools.Add(pool);
                                        poolCounter++;
                                    }
                                }
                                else
                                {
                                    logger.LogDebug($"  Pool key {redisKey} in index but has no value");
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug($"  Failed to deserialize pool key {member}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        logger.LogDebug($"No pool index found at {poolIndexKey}, trying key scan...");
                        int scannedKeys = 0;
                        foreach (var key in server.Keys(pattern: poolKeyPrefix + "*", database: redisDbId))
                        {
                            scannedKeys++;
                            try
                            {
                                // skip index key if raw scan
                                if (key.ToString().EndsWith(":index")) continue;
                                var val = await db.StringGetAsync(key);
                                if (!val.IsNullOrEmpty)
                                {
                                    var pool = JsonSerializer.Deserialize<Pool>((string)val!);
                                    if (pool != null)
                                    {
                                        preloadPools.Add(pool);
                                        poolCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug($"  Failed to deserialize pool key {key}: {ex.Message}");
                            }
                        }
                        logger.LogDebug($"Scanned {scannedKeys} keys matching pattern {poolKeyPrefix}*");
                    }
                    logger.LogDebug($"Loaded {poolCounter} pools from Redis.");

                    // Optional seeding if no pools loaded
                    if (poolCounter == 0 && enableSeeding && File.Exists(seedPoolsFile))
                    {
                        logger.LogDebug($"Seeding pools from file: {seedPoolsFile}");
                        try
                        {
                            var json = await File.ReadAllTextAsync(seedPoolsFile);
                            var seedPools = JsonSerializer.Deserialize<Pool[]>((string)json) ?? Array.Empty<Pool>();
                            foreach (var p in seedPools)
                            {
                                var redisKey = poolKeyPrefix + p.PoolAddress;
                                var poolJson = JsonSerializer.Serialize(p);
                                await db.StringSetAsync(redisKey, poolJson);
                                await db.SetAddAsync(poolIndexKey, p.PoolAddress);
                                preloadPools.Add(p);
                            }
                            poolCounter = preloadPools.Count;
                            logger.LogDebug($"Seeded {poolCounter} pools.");
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug($"Failed to seed pools: {ex.Message}");
                        }
                    }

                    // Load Aggregated Pools using index set if available
                    logger.LogDebug("Preloading aggregated pools from Redis (if any)...");
                    int aggCounter = 0;
                    var aggIndexKey = aggregatedPoolKeyPrefix + "index";
                    if (await db.KeyExistsAsync(aggIndexKey))
                    {
                        var members = await db.SetMembersAsync(aggIndexKey);
                        foreach (var member in members)
                        {
                            try
                            {
                                var redisKey = aggregatedPoolKeyPrefix + member.ToString();
                                var val = await db.StringGetAsync(redisKey);
                                if (!val.IsNullOrEmpty)
                                {
                                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>((string)val!);
                                    if (aggregatedPool != null)
                                    {
                                        preloadAggregatedPools.Add(aggregatedPool);
                                        aggCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug($"  Failed to deserialize aggregated pool key {member}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        foreach (var key in server.Keys(pattern: aggregatedPoolKeyPrefix + "*", database: redisDbId))
                        {
                            try
                            {
                                if (key.ToString().EndsWith(":index")) continue;
                                var val = await db.StringGetAsync(key);
                                if (!val.IsNullOrEmpty)
                                {
                                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>((string)val!);
                                    if (aggregatedPool != null)
                                    {
                                        preloadAggregatedPools.Add(aggregatedPool);
                                        aggCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug($"  Failed to deserialize aggregated pool key {key}: {ex.Message}");
                            }
                        }
                    }
                    logger.LogDebug($"Loaded {aggCounter} aggregated pools from Redis.");

                    if (aggCounter == 0 && enableSeeding && !string.IsNullOrEmpty(seedAggregatedFile) && File.Exists(seedAggregatedFile))
                    {
                        logger.LogDebug($"Seeding aggregated pools from file: {seedAggregatedFile}");
                        try
                        {
                            var json = await File.ReadAllTextAsync(seedAggregatedFile);
                            var seedAggs = JsonSerializer.Deserialize<AggregatedPool[]>((string)json) ?? Array.Empty<AggregatedPool>();
                            foreach (var ap in seedAggs)
                            {
                                var redisKey = aggregatedPoolKeyPrefix + ap.AssetIdA + "-" + ap.AssetIdB;
                                var apJson = JsonSerializer.Serialize(ap);
                                await db.StringSetAsync(redisKey, apJson);
                                await db.SetAddAsync(aggIndexKey, ap.AssetIdA + "-" + ap.AssetIdB);
                                preloadAggregatedPools.Add(ap);
                            }
                            aggCounter = preloadAggregatedPools.Count;
                            logger.LogDebug($"Seeded {aggCounter} aggregated pools.");
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug($"Failed to seed aggregated pools: {ex.Message}");
                        }
                    }

                    logger.LogDebug("");

                    // Optional summary output (limit to first 5 to avoid spam)
                    if (poolCounter > 0)
                    {
                        logger.LogDebug("Sample preloaded pools (up to 5):");
                        foreach (var p in preloadPools.Take(5))
                        {
                            logger.LogDebug($"  {p.PoolAddress} | {p.AssetIdA}-{p.AssetIdB} | Protocol={p.Protocol} | RealA={p.RealAmountA:F4} RealB={p.RealAmountB:F4}");
                        }
                        logger.LogDebug("");
                    }
                    if (aggCounter > 0)
                    {
                        logger.LogDebug("Sample preloaded aggregated pools (up to 5):");
                        foreach (var ap in preloadAggregatedPools.Take(5))
                        {
                            logger.LogDebug($"  {ap.AssetIdA}-{ap.AssetIdB} | Pools={ap.PoolCount} | VSumA(L1)={ap.VirtualSumALevel1:F4} VSumB(L1)={ap.VirtualSumBLevel1:F4}");
                        }
                        logger.LogDebug("");
                    }
                }
                else
                {
                    logger.LogDebug("No Redis endpoints found to scan keys.");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug($"Warning: Failed to preload pools from Redis: {ex.Message}");
            }

            // Subscribe to pool updates
            await subscriber.SubscribeAsync(RedisChannel.Literal(poolChannel), (channel, message) =>
            {
                try
                {
                    var pool = JsonSerializer.Deserialize<Pool>((string)message!);
                    if (pool != null)
                    {
                        logger.LogDebug($"[{DateTime.UtcNow:HH:mm:ss.fff}] POOL UPDATE:");
                        logger.LogDebug($"  Pool Address: {pool.PoolAddress}");
                        logger.LogDebug($"  Pool App ID:  {pool.PoolAppId}");
                        logger.LogDebug($"  Protocol:     {pool.Protocol}");
                        logger.LogDebug($"  Asset A ID:   {pool.AssetIdA}");
                        logger.LogDebug($"  Asset B ID:   {pool.AssetIdB}");
                        logger.LogDebug($"  Real Amount A: {pool.RealAmountA:F6}");
                        logger.LogDebug($"  Real Amount B: {pool.RealAmountB:F6}");
                        logger.LogDebug($"  Virtual Amount A: {pool.VirtualAmountA:F6}");
                        logger.LogDebug($"  Virtual Amount B: {pool.VirtualAmountB:F6}");
                        if (pool.Timestamp.HasValue)
                        {
                            logger.LogDebug($"  Timestamp:    {pool.Timestamp.Value:yyyy-MM-dd HH:mm:ss UTC}");
                        }
                        logger.LogDebug("");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug($"Error processing pool update: {ex.Message}");
                }
            });

            logger.LogDebug($"Subscribed to pool updates on channel: {poolChannel}");

            // Subscribe to aggregated pool updates
            await subscriber.SubscribeAsync(RedisChannel.Literal(aggregatedPoolChannel), (channel, message) =>
            {
                try
                {
                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>((string)message!);
                    if (aggregatedPool != null)
                    {
                        logger.LogDebug($"[{DateTime.UtcNow:HH:mm:ss.fff}] AGGREGATED POOL UPDATE:");
                        logger.LogDebug($"  Asset Pair:   {aggregatedPool.AssetIdA} - {aggregatedPool.AssetIdB}");
                        logger.LogDebug($"  Pool Count:   {aggregatedPool.PoolCount}");
                        logger.LogDebug($"  Virtual Sum A (Level 1): {aggregatedPool.VirtualSumALevel1:F6}");
                        logger.LogDebug($"  Virtual Sum B (Level 1): {aggregatedPool.VirtualSumBLevel1:F6}");
                        logger.LogDebug($"  Virtual Sum A (Level 2): {aggregatedPool.VirtualSumALevel2:F6}");
                        logger.LogDebug($"  Virtual Sum B (Level 2): {aggregatedPool.VirtualSumBLevel2:F6}");
                        logger.LogDebug($"  Total Virtual A: {aggregatedPool.VirtualSumA:F6}");
                        logger.LogDebug($"  Total Virtual B: {aggregatedPool.VirtualSumB:F6}");
                        logger.LogDebug($"  TVL A:        {aggregatedPool.TVL_A:F6}");
                        logger.LogDebug($"  TVL B:        {aggregatedPool.TVL_B:F6}");
                        if (aggregatedPool.LastUpdated.HasValue)
                        {
                            logger.LogDebug($"  Last Updated: {aggregatedPool.LastUpdated.Value:yyyy-MM-dd HH:mm:ss UTC}");
                        }
                        logger.LogDebug("");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug($"Error processing aggregated pool update: {ex.Message}");
                }
            });

            logger.LogDebug($"Subscribed to aggregated pool updates on channel: {aggregatedPoolChannel}");
            logger.LogDebug("");
            logger.LogDebug("Listening for updates... Press Ctrl+C to exit.");
            logger.LogDebug("");
 
            // Keep the application running
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("");
            logger.LogDebug("Shutting down gracefully...");
        }
        catch (Exception ex)
        {
            logger.LogDebug($"Fatal error: {ex.Message}");
            logger.LogDebug($"Stack trace: {ex.StackTrace}");
            Environment.Exit(1);
        }
    }
}
