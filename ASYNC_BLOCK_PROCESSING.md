# Asynchronous Block Processing Implementation

## Overview

This implementation adds asynchronous block processing to the `TradeReporterBackgroundService` to prevent the service from falling behind when block processing takes longer than the block time (approximately 3.3 seconds on Algorand).

## Key Features

### 1. Asynchronous Processing
- Block processing tasks run in the background without blocking the main execution loop
- The service can start processing new blocks even if previous blocks are still being processed
- Maintains data consistency through proper synchronization

### 2. Memory Throttling
- Monitors system memory usage to prevent excessive resource consumption
- Automatically falls back to synchronous processing when memory usage exceeds the threshold
- Configurable memory threshold and check intervals

### 3. Concurrency Control
- Uses `SemaphoreSlim` to limit the number of concurrent block processing tasks
- Prevents system overload by maintaining a reasonable number of concurrent operations
- Configurable maximum concurrent tasks

### 4. Graceful Degradation
- Falls back to synchronous processing when:
  - Memory pressure is high
  - Maximum concurrent tasks limit is reached
  - Async processing is disabled in configuration

## Configuration

Add the following to your `appsettings.json`:

```json
{
  "AppConfiguration": {
    "BlockProcessing": {
      "EnableAsyncProcessing": true,
      "MaxConcurrentTasks": 3,
      "MemoryThresholdMB": 1024,
      "MemoryCheckIntervalMs": 5000
    }
  }
}
```

### Configuration Options

- **EnableAsyncProcessing**: Enable/disable asynchronous block processing (default: true)
- **MaxConcurrentTasks**: Maximum number of concurrent block processing tasks (default: 3)
- **MemoryThresholdMB**: Memory threshold in MB above which async processing is disabled (default: 1024)
- **MemoryCheckIntervalMs**: How often to check memory usage in milliseconds (default: 5000)

## Implementation Details

### Memory Monitoring

The service monitors memory usage using `Process.GetCurrentProcess().WorkingSet64` and checks it at configurable intervals. When memory usage exceeds the threshold, async processing is temporarily disabled.

### Task Management

- Running tasks are tracked in a thread-safe list
- Completed tasks are automatically cleaned up
- During shutdown, the service waits for all running tasks to complete

### Error Handling

- Each async task is wrapped in proper exception handling
- Errors in one block's processing don't affect other blocks
- Cancellation tokens are properly propagated

## Performance Benefits

### Before (Synchronous)
```
Block 1: ████████████████████████████ (3s)
Block 2:                              ████████████████████████████ (3s)
Block 3:                                                            ████████████████████████████ (3s)
Total: 9 seconds for 3 blocks
```

### After (Asynchronous)
```
Block 1: ████████████████████████████ (3s)
Block 2:    ████████████████████████████ (3s)
Block 3:       ████████████████████████████ (3s)
Total: ~5 seconds for 3 blocks
```

## Monitoring

The implementation includes detailed logging to monitor:
- Memory usage and threshold checks
- Task start/completion events
- Fallback to synchronous processing
- Graceful shutdown behavior

Set log level to `Debug` for the `TradeReporterBackgroundService` to see detailed async processing logs:

```json
{
  "Logging": {
    "LogLevel": {
      "AVMTradeReporter.Services.TradeReporterBackgroundService": "Debug"
    }
  }
}
```

## Safety Considerations

1. **Data Consistency**: Trade and liquidity data structures use `ConcurrentDictionary` for thread safety
2. **Resource Limits**: Memory monitoring prevents runaway resource consumption
3. **Graceful Shutdown**: Service waits for all tasks to complete before stopping
4. **Error Isolation**: Exceptions in one block don't affect others
5. **Configurable Limits**: All thresholds are configurable for different environments

## Testing

The implementation has been tested to ensure:
- ✅ Application starts successfully with new configuration
- ✅ Async processing is triggered when enabled
- ✅ Memory monitoring works correctly
- ✅ Graceful fallback to synchronous processing
- ✅ Proper task cleanup and shutdown behavior
- ✅ No regressions in existing functionality