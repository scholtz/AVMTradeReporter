using AVMTradeReporter.Models.Data;
using StackExchange.Redis;
using System.Text.Json;
using System.Collections.Concurrent;
using System.IO; // added for optional seed file

namespace AVMTradeReporter.Subscriber;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("AVM Trade Reporter - Redis PubSub Subscriber");
        Console.WriteLine("=============================================");
        Console.WriteLine();

        // Configuration - can be overridden via command line arguments or environment variables
        var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
        var poolChannel = Environment.GetEnvironmentVariable("POOL_CHANNEL") ?? "avmtrade:pool:updates";
        var aggregatedPoolChannel = Environment.GetEnvironmentVariable("AGGREGATED_POOL_CHANNEL") ?? "avmtrade:aggregatedpool:updates";
        var poolKeyPrefix = Environment.GetEnvironmentVariable("POOL_KEY_PREFIX") ?? "avmtrade:pools:"; // matches AppConfiguration.Redis.KeyPrefix
        var aggregatedPoolKeyPrefix = Environment.GetEnvironmentVariable("AGGREGATED_POOL_KEY_PREFIX") ?? "avmtrade:aggregatedpools:"; // assumed prefix for persisted aggregated pools if available
        var seedPoolsFile = Environment.GetEnvironmentVariable("SEED_POOLS_FILE") ?? "AVMTradeReporterTests/Data/pools-algo-usdc.json"; // optional
        var seedAggregatedFile = Environment.GetEnvironmentVariable("SEED_AGGREGATED_POOLS_FILE") ?? string.Empty; // optional JSON array of AggregatedPool
        var enableSeeding = (Environment.GetEnvironmentVariable("ENABLE_REDIS_SEEDING") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"Connecting to Redis at: {redisConnectionString}");
        Console.WriteLine($"Pool updates channel: {poolChannel}");
        Console.WriteLine($"Aggregated pool updates channel: {aggregatedPoolChannel}");
        Console.WriteLine($"Pool key prefix: {poolKeyPrefix}");
        Console.WriteLine($"Aggregated pool key prefix: {aggregatedPoolKeyPrefix}");
        Console.WriteLine($"Enable seeding: {enableSeeding}");
        Console.WriteLine();

        try
        {
            // Connect to Redis (add allowAdmin for key scan if not provided)
            if (!redisConnectionString.Contains("allowAdmin", StringComparison.OrdinalIgnoreCase))
            {
                redisConnectionString = redisConnectionString.Contains(",") ? redisConnectionString + ",allowAdmin=true" : redisConnectionString + ",allowAdmin=true";
            }
            var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var subscriber = redis.GetSubscriber();
            var db = redis.GetDatabase();

            Console.WriteLine("Connected to Redis successfully!");
            Console.WriteLine();

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
                    Console.WriteLine("Preloading pools from Redis...");
                    int poolCounter = 0;
                    var poolIndexKey = poolKeyPrefix + "index";
                    if (await db.KeyExistsAsync(poolIndexKey))
                    {
                        var members = await db.SetMembersAsync(poolIndexKey);
                        foreach (var member in members)
                        {
                            try
                            {
                                var redisKey = poolKeyPrefix + member;
                                var val = await db.StringGetAsync(redisKey);
                                if (!val.IsNullOrEmpty)
                                {
                                    var pool = JsonSerializer.Deserialize<Pool>(val!);
                                    if (pool != null)
                                    {
                                        preloadPools.Add(pool);
                                        poolCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkRed;
                                Console.WriteLine($"  Failed to deserialize pool key {member}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                    }
                    else
                    {
                        foreach (var key in server.Keys(pattern: poolKeyPrefix + "*"))
                        {
                            try
                            {
                                // skip index key if raw scan
                                if (key.ToString().EndsWith("index")) continue;
                                var val = await db.StringGetAsync(key);
                                if (!val.IsNullOrEmpty)
                                {
                                    var pool = JsonSerializer.Deserialize<Pool>(val!);
                                    if (pool != null)
                                    {
                                        preloadPools.Add(pool);
                                        poolCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkRed;
                                Console.WriteLine($"  Failed to deserialize pool key {key}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                    }
                    Console.WriteLine($"Loaded {poolCounter} pools from Redis.");

                    // Optional seeding if no pools loaded
                    if (poolCounter == 0 && enableSeeding && File.Exists(seedPoolsFile))
                    {
                        Console.WriteLine($"Seeding pools from file: {seedPoolsFile}");
                        try
                        {
                            var json = await File.ReadAllTextAsync(seedPoolsFile);
                            var seedPools = JsonSerializer.Deserialize<Pool[]>(json) ?? Array.Empty<Pool>();
                            foreach (var p in seedPools)
                            {
                                var redisKey = poolKeyPrefix + p.PoolAddress;
                                var poolJson = JsonSerializer.Serialize(p);
                                await db.StringSetAsync(redisKey, poolJson);
                                await db.SetAddAsync(poolIndexKey, p.PoolAddress);
                                preloadPools.Add(p);
                            }
                            poolCounter = preloadPools.Count;
                            Console.WriteLine($"Seeded {poolCounter} pools.");
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Failed to seed pools: {ex.Message}");
                            Console.ResetColor();
                        }
                    }

                    // Load Aggregated Pools using index set if available
                    Console.WriteLine("Preloading aggregated pools from Redis (if any)...");
                    int aggCounter = 0;
                    var aggIndexKey = aggregatedPoolKeyPrefix + "index";
                    if (await db.KeyExistsAsync(aggIndexKey))
                    {
                        var members = await db.SetMembersAsync(aggIndexKey);
                        foreach (var member in members)
                        {
                            try
                            {
                                var redisKey = aggregatedPoolKeyPrefix + member;
                                var val = await db.StringGetAsync(redisKey);
                                if (!val.IsNullOrEmpty)
                                {
                                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>(val!);
                                    if (aggregatedPool != null)
                                    {
                                        preloadAggregatedPools.Add(aggregatedPool);
                                        aggCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkRed;
                                Console.WriteLine($"  Failed to deserialize aggregated pool key {member}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                    }
                    else
                    {
                        foreach (var key in server.Keys(pattern: aggregatedPoolKeyPrefix + "*"))
                        {
                            try
                            {
                                if (key.ToString().EndsWith("index")) continue;
                                var val = await db.StringGetAsync(key);
                                if (!val.IsNullOrEmpty)
                                {
                                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>(val!);
                                    if (aggregatedPool != null)
                                    {
                                        preloadAggregatedPools.Add(aggregatedPool);
                                        aggCounter++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkRed;
                                Console.WriteLine($"  Failed to deserialize aggregated pool key {key}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                    }
                    Console.WriteLine($"Loaded {aggCounter} aggregated pools from Redis.");

                    if (aggCounter == 0 && enableSeeding && !string.IsNullOrEmpty(seedAggregatedFile) && File.Exists(seedAggregatedFile))
                    {
                        Console.WriteLine($"Seeding aggregated pools from file: {seedAggregatedFile}");
                        try
                        {
                            var json = await File.ReadAllTextAsync(seedAggregatedFile);
                            var seedAggs = JsonSerializer.Deserialize<AggregatedPool[]>(json) ?? Array.Empty<AggregatedPool>();
                            foreach (var ap in seedAggs)
                            {
                                var redisKey = aggregatedPoolKeyPrefix + ap.AssetIdA + "-" + ap.AssetIdB;
                                var apJson = JsonSerializer.Serialize(ap);
                                await db.StringSetAsync(redisKey, apJson);
                                await db.SetAddAsync(aggIndexKey, ap.AssetIdA + "-" + ap.AssetIdB);
                                preloadAggregatedPools.Add(ap);
                            }
                            aggCounter = preloadAggregatedPools.Count;
                            Console.WriteLine($"Seeded {aggCounter} aggregated pools.");
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Failed to seed aggregated pools: {ex.Message}");
                            Console.ResetColor();
                        }
                    }

                    Console.WriteLine();

                    // Optional summary output (limit to first 5 to avoid spam)
                    if (poolCounter > 0)
                    {
                        Console.WriteLine("Sample preloaded pools (up to 5):");
                        foreach (var p in preloadPools.Take(5))
                        {
                            Console.WriteLine($"  {p.PoolAddress} | {p.AssetIdA}-{p.AssetIdB} | Protocol={p.Protocol} | RealA={p.RealAmountA:F4} RealB={p.RealAmountB:F4}");
                        }
                        Console.WriteLine();
                    }
                    if (aggCounter > 0)
                    {
                        Console.WriteLine("Sample preloaded aggregated pools (up to 5):");
                        foreach (var ap in preloadAggregatedPools.Take(5))
                        {
                            Console.WriteLine($"  {ap.AssetIdA}-{ap.AssetIdB} | Pools={ap.PoolCount} | VSumA(L1)={ap.VirtualSumALevel1:F4} VSumB(L1)={ap.VirtualSumBLevel1:F4}");
                        }
                        Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine("No Redis endpoints found to scan keys.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Failed to preload pools from Redis: {ex.Message}");
                Console.ResetColor();
            }

            // Subscribe to pool updates
            await subscriber.SubscribeAsync(RedisChannel.Literal(poolChannel), (channel, message) =>
            {
                try
                {
                    var pool = JsonSerializer.Deserialize<Pool>(message!);
                    if (pool != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] POOL UPDATE:");
                        Console.ResetColor();
                        Console.WriteLine($"  Pool Address: {pool.PoolAddress}");
                        Console.WriteLine($"  Pool App ID:  {pool.PoolAppId}");
                        Console.WriteLine($"  Protocol:     {pool.Protocol}");
                        Console.WriteLine($"  Asset A ID:   {pool.AssetIdA}");
                        Console.WriteLine($"  Asset B ID:   {pool.AssetIdB}");
                        Console.WriteLine($"  Real Amount A: {pool.RealAmountA:F6}");
                        Console.WriteLine($"  Real Amount B: {pool.RealAmountB:F6}");
                        Console.WriteLine($"  Virtual Amount A: {pool.VirtualAmountA:F6}");
                        Console.WriteLine($"  Virtual Amount B: {pool.VirtualAmountB:F6}");
                        if (pool.Timestamp.HasValue)
                        {
                            Console.WriteLine($"  Timestamp:    {pool.Timestamp.Value:yyyy-MM-dd HH:mm:ss UTC}");
                        }
                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error processing pool update: {ex.Message}");
                    Console.ResetColor();
                }
            });

            Console.WriteLine($"Subscribed to pool updates on channel: {poolChannel}");

            // Subscribe to aggregated pool updates
            await subscriber.SubscribeAsync(RedisChannel.Literal(aggregatedPoolChannel), (channel, message) =>
            {
                try
                {
                    var aggregatedPool = JsonSerializer.Deserialize<AggregatedPool>(message!);
                    if (aggregatedPool != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] AGGREGATED POOL UPDATE:");
                        Console.ResetColor();
                        Console.WriteLine($"  Asset Pair:   {aggregatedPool.AssetIdA} - {aggregatedPool.AssetIdB}");
                        Console.WriteLine($"  Pool Count:   {aggregatedPool.PoolCount}");
                        Console.WriteLine($"  Virtual Sum A (Level 1): {aggregatedPool.VirtualSumALevel1:F6}");
                        Console.WriteLine($"  Virtual Sum B (Level 1): {aggregatedPool.VirtualSumBLevel1:F6}");
                        Console.WriteLine($"  Virtual Sum A (Level 2): {aggregatedPool.VirtualSumALevel2:F6}");
                        Console.WriteLine($"  Virtual Sum B (Level 2): {aggregatedPool.VirtualSumBLevel2:F6}");
                        Console.WriteLine($"  Total Virtual A: {aggregatedPool.VirtualSumA:F6}");
                        Console.WriteLine($"  Total Virtual B: {aggregatedPool.VirtualSumB:F6}");
                        Console.WriteLine($"  TVL A:        {aggregatedPool.TVL_A:F6}");
                        Console.WriteLine($"  TVL B:        {aggregatedPool.TVL_B:F6}");
                        if (aggregatedPool.LastUpdated.HasValue)
                        {
                            Console.WriteLine($"  Last Updated: {aggregatedPool.LastUpdated.Value:yyyy-MM-dd HH:mm:ss UTC}");
                        }
                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error processing aggregated pool update: {ex.Message}");
                    Console.ResetColor();
                }
            });

            Console.WriteLine($"Subscribed to aggregated pool updates on channel: {aggregatedPoolChannel}");
            Console.WriteLine();
            Console.WriteLine("Listening for updates... Press Ctrl+C to exit.");
            Console.WriteLine();

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
            Console.WriteLine();
            Console.WriteLine("Shutting down gracefully...");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }
}
