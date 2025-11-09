# AVM Trade Reporter Subscriber

A sample C# console application demonstrating how to subscribe to Redis PubSub channels and receive real-time pool and aggregated pool updates from the AVM Trade Reporter.

## Overview

This subscriber listens to two Redis PubSub channels:
- **Pool Updates**: Individual pool state updates from various DEX protocols (Pact, Tiny, Biatec)
- **Aggregated Pool Updates**: Aggregated liquidity data for asset pairs across all pools

## Prerequisites

- .NET 8.0 SDK or later
- Access to a Redis server (default: localhost:6379)
- Running instance of AVMTradeReporter with Redis PubSub enabled

## Configuration

The subscriber can be configured using environment variables:

- `REDIS_CONNECTION`: Redis connection string (default: `localhost:6379`)
- `POOL_CHANNEL`: Pool updates channel name (default: `avmtrade:pool:updates`)
- `AGGREGATED_POOL_CHANNEL`: Aggregated pool updates channel name (default: `avmtrade:aggregatedpool:updates`)

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run --project AVMTradeReporter.Subscriber
```

Or with custom configuration:

```bash
export REDIS_CONNECTION="your-redis-host:6379"
export POOL_CHANNEL="your-pool-channel"
export AGGREGATED_POOL_CHANNEL="your-aggregated-pool-channel"
dotnet run --project AVMTradeReporter.Subscriber
```

## Example Output

```
AVM Trade Reporter - Redis PubSub Subscriber
=============================================

Connecting to Redis at: localhost:6379
Pool updates channel: avmtrade:pool:updates
Aggregated pool updates channel: avmtrade:aggregatedpool:updates

Connected to Redis successfully!

Subscribed to pool updates on channel: avmtrade:pool:updates
Subscribed to aggregated pool updates on channel: avmtrade:aggregatedpool:updates

Listening for updates... Press Ctrl+C to exit.

[12:34:56.789] POOL UPDATE:
  Pool Address: ABC123...
  Pool App ID:  12345
  Protocol:     Pact
  Asset A ID:   0
  Asset B ID:   31566704
  Real Amount A: 1000.123456
  Real Amount B: 50000.789012
  Virtual Amount A: 1000.123456
  Virtual Amount B: 50000.789012
  Timestamp:    2025-11-04 12:34:56 UTC

[12:34:57.123] AGGREGATED POOL UPDATE:
  Asset Pair:   0 - 31566704
  Pool Count:   15
  Virtual Sum A (Level 1): 15000.567890
  Virtual Sum B (Level 1): 750000.123456
  Virtual Sum A (Level 2): 2500.123456
  Virtual Sum B (Level 2): 125000.789012
  Total Virtual A: 17500.691346
  Total Virtual B: 875000.912468
  TVL A:        15000.567890
  TVL B:        750000.123456
  Last Updated: 2025-11-04 12:34:57 UTC
```

## Using the Models Library

This project references `AVMTradeReporter.Models` which contains the data models:
- `Pool`: Individual pool state
- `AggregatedPool`: Aggregated liquidity data for asset pairs
- Various enums for DEX protocols, AMM types, etc.

You can use this models library in your own projects to consume pool data from Redis:

```bash
dotnet add package AVMTradeReporter.Models
```

## Integration in Your Application

To integrate pool data subscription in your own application:

1. Add the `AVMTradeReporter.Models` NuGet package
2. Add the `StackExchange.Redis` NuGet package
3. Subscribe to the Redis channels as shown in this sample
4. Deserialize the JSON messages into `Pool` or `AggregatedPool` objects

```csharp
using AVMTradeReporter.Models.Data;
using StackExchange.Redis;
using System.Text.Json;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var subscriber = redis.GetSubscriber();

await subscriber.SubscribeAsync(
    RedisChannel.Literal("avmtrade:pool:updates"), 
    (channel, message) =>
{
    var pool = JsonSerializer.Deserialize<Pool>(message!);
    // Process pool update
});
```

## License

MIT
