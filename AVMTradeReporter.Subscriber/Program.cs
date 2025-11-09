using AVMTradeReporter.Models.Data;
using StackExchange.Redis;
using System.Text.Json;

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

        Console.WriteLine($"Connecting to Redis at: {redisConnectionString}");
        Console.WriteLine($"Pool updates channel: {poolChannel}");
        Console.WriteLine($"Aggregated pool updates channel: {aggregatedPoolChannel}");
        Console.WriteLine();

        try
        {
            // Connect to Redis
            var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var subscriber = redis.GetSubscriber();

            Console.WriteLine("Connected to Redis successfully!");
            Console.WriteLine();

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
