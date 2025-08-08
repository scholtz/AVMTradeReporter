# Pool Repository with In-Memory Cache and Redis Persistence

## Overview

The PoolRepository has been enhanced to implement a high-performance caching solution that:

1. **Keeps all pools in memory** using `ConcurrentDictionary<string, Pool>`
2. **Persists to Redis** for fast application startup and data durability
3. **Backs up to Elasticsearch** for long-term storage and querying
4. **Initializes on application startup** by loading pools from Redis first, then Elasticsearch if Redis is empty

## Key Features

### In-Memory Cache
- Uses `ConcurrentDictionary<string, Pool>` for thread-safe, high-performance access
- All pool operations (read/write) happen against the in-memory cache first
- Pool ID format: `{PoolAddress}_{PoolAppId}_{Protocol}`

### Redis Integration
- Asynchronous persistence to Redis for durability
- JSON serialization for human-readable storage
- Configurable key prefixes and database selection
- Loads all pools from Redis on application startup

### Elasticsearch Backup
- Continues to use Elasticsearch for long-term storage
- Asynchronous writes to avoid blocking operations
- Falls back to Elasticsearch if Redis is unavailable

### Smart Initialization
- On startup, attempts to load from Redis first (fastest)
- If Redis is empty or unavailable, loads from Elasticsearch
- If loading from Elasticsearch, automatically populates Redis for future starts

## Configuration

Add Redis configuration to your `appsettings.json`:

```json
{
  "AppConfiguration": {
    "Redis": {
      "ConnectionString": "localhost:6379",
      "KeyPrefix": "avmtrade:pools:",
      "Enabled": true,
      "DatabaseId": 0
    }
  }
}
```

### Configuration Options

- **ConnectionString**: Redis server connection string
- **KeyPrefix**: Prefix for all Redis keys (default: "avmtrade:pools:")
- **Enabled**: Enable/disable Redis functionality
- **DatabaseId**: Redis database number (default: 0)

## API Enhancements

### New Endpoints

#### Get Pool Statistics
```
GET /api/pool/stats
```
Returns comprehensive statistics about pools:
```json
{
  "totalPools": 150,
  "protocolStats": {
    "Pact": 45,
    "Tiny": 60,
    "Biatec": 45
  },
  "lastUpdated": "2024-01-01T12:00:00Z"
}
```

### Enhanced Endpoints

All existing endpoints now benefit from in-memory performance:
- `GET /api/pool` - Get all pools (filtered and paginated)
- `GET /api/pool/{poolAddress}/{poolAppId}/{protocol}` - Get specific pool
- `GET /api/pool/by-protocol/{protocol}` - Get pools by protocol

## Performance Benefits

### Read Performance
- **Memory access**: Sub-millisecond response times
- **No database queries**: Eliminated for read operations
- **Concurrent access**: Thread-safe operations

### Write Performance
- **Non-blocking writes**: Asynchronous persistence to Redis and Elasticsearch
- **Immediate updates**: In-memory cache updated instantly
- **Background persistence**: External storage happens asynchronously

### Startup Performance
- **Fast initialization**: Redis loading is significantly faster than Elasticsearch
- **Fallback mechanism**: Graceful degradation if Redis is unavailable
- **One-time cost**: Initialization happens once per application start

## Implementation Details

### Key Components

1. **PoolRepository**: Main repository class with in-memory cache
2. **IPoolRepository**: Interface for dependency injection
3. **Redis Configuration**: Configuration model for Redis settings
4. **Initialization Logic**: Smart loading from multiple sources

### Thread Safety
- Uses `ConcurrentDictionary` for thread-safe operations
- Uses `SemaphoreSlim` for initialization synchronization
- Asynchronous operations don't block each other

### Error Handling
- Graceful fallback if Redis is unavailable
- Continues operation even if external persistence fails
- Comprehensive logging for troubleshooting

### Data Flow

```
Application Startup:
1. Try loading from Redis
2. If Redis empty/unavailable, load from Elasticsearch
3. If loaded from Elasticsearch, save to Redis

Pool Updates:
1. Update in-memory cache immediately
2. Asynchronously save to Redis
3. Asynchronously save to Elasticsearch
4. Publish to SignalR hub

Pool Reads:
1. Return directly from in-memory cache
2. No external database access required
```

## Dependencies Added

- **StackExchange.Redis**: Redis client library
- **System.Text.Json**: JSON serialization for Redis storage

## Monitoring and Debugging

### Logging
- Detailed logs for initialization process
- Error logs for Redis/Elasticsearch failures
- Performance logs for loading times

### Health Checks
- Pool count tracking
- Initialization status monitoring
- Redis connection health

## Usage Examples

### Checking Pool Statistics
```csharp
var stats = await _poolRepository.GetPoolCountAsync();
Console.WriteLine($"Total pools in memory: {stats}");
```

### Fast Pool Lookup
```csharp
var pool = await _poolRepository.GetPoolAsync("POOL_ADDRESS", 123456, DEXProtocol.Biatec, cancellationToken);
// Returns immediately from memory
```

### Protocol Filtering
```csharp
var biatecPools = await _poolRepository.GetPoolsAsync(DEXProtocol.Biatec, 50, cancellationToken);
// Filtered in-memory, no database query
```

## Benefits Summary

1. **Performance**: Sub-millisecond read operations
2. **Scalability**: No database load for read operations
3. **Reliability**: Multiple persistence layers with fallback
4. **Flexibility**: Easy to disable Redis if needed
5. **Monitoring**: Comprehensive logging and statistics
6. **Developer Experience**: Simple API with powerful caching underneath