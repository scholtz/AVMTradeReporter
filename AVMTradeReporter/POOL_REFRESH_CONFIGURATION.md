# Pool Refresh Background Service Configuration

## Overview

The Pool Refresh Background Service is designed to periodically refresh all pools in the system by querying the blockchain directly. This service is fully configurable through the `appsettings.json` file and will only run when explicitly enabled.

## Configuration

The pool refresh service is controlled through the `PoolRefresh` section in the `AppConfiguration`:

```json
{
  "AppConfiguration": {
    "PoolRefresh": {
      "Enabled": true,
      "IntervalHours": 24,
      "DelayBetweenPoolsSeconds": 5,
      "InitialDelayMinutes": 1
    }
  }
}
```

### Configuration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | boolean | `true` | **Controls whether the background service runs at all**. If set to `false`, the service will log a message and exit immediately. |
| `IntervalHours` | integer | `24` | How often to perform a full refresh of all pools (in hours). Default is 24 hours (once per day). |
| `DelayBetweenPoolsSeconds` | integer | `5` | Delay between processing each individual pool (in seconds). This helps prevent overwhelming the Algorand network. |
| `InitialDelayMinutes` | integer | `1` | Initial delay before starting the first refresh cycle (in minutes). Allows other services to initialize first. |

## Behavior

### When Enabled (`Enabled: true`)
1. **Startup**: Service waits for the initial delay, then begins monitoring
2. **Refresh Cycle**: Every `IntervalHours`, the service will:
   - Load all pools from the repository
   - Process each pool using the appropriate processor (Pact, Tiny, or Biatec)
   - Wait `DelayBetweenPoolsSeconds` between each pool to avoid rate limiting
   - Update the pool information in the repository
   - Log detailed progress and statistics

### When Disabled (`Enabled: false`)
1. **Startup**: Service logs "Pool Refresh Background Service is disabled via configuration."
2. **Exit**: Service returns immediately without doing any work

## Example Configurations

### Production Configuration (Daily Refresh)
```json
{
  "PoolRefresh": {
    "Enabled": true,
    "IntervalHours": 24,
    "DelayBetweenPoolsSeconds": 5,
    "InitialDelayMinutes": 5
  }
}
```

### Development Configuration (Frequent Testing)
```json
{
  "PoolRefresh": {
    "Enabled": true,
    "IntervalHours": 1,
    "DelayBetweenPoolsSeconds": 2,
    "InitialDelayMinutes": 1
  }
}
```

### Disabled Configuration
```json
{
  "PoolRefresh": {
    "Enabled": false
  }
}
```

## Logging

The service provides comprehensive logging at different levels:

### Information Level
- Service startup/shutdown messages
- Refresh cycle start/completion with timestamps
- Summary statistics after each refresh cycle

### Debug Level
- Individual pool refresh progress
- Detailed processing information

### Warning Level
- Individual pool refresh failures (continues processing other pools)
- No pools found to refresh

### Error Level
- Critical service failures
- Configuration issues

## Error Handling

The service is designed to be resilient:

1. **Individual Pool Failures**: If one pool fails to refresh, the service logs a warning and continues with the next pool
2. **Service Failures**: If the entire service encounters an error, it waits 30 minutes before retrying
3. **Cancellation**: Respects cancellation tokens for graceful shutdown

## Dependencies

The service requires the following components to be properly configured:

1. **Pool Repository**: For loading and storing pool information
2. **Algorand API Client**: For querying blockchain data
3. **Pool Processors**: For each protocol (Pact, Tiny, Biatec)
4. **Configuration**: Proper `AppConfiguration` setup

## Performance Considerations

### Network Impact
- The `DelayBetweenPoolsSeconds` setting helps prevent overwhelming the Algorand network
- Recommended minimum delay is 5 seconds for production environments
- Consider the total number of pools when setting this value

### Resource Usage
- Each pool refresh requires blockchain API calls
- Memory usage is minimal as pools are processed sequentially
- Elasticsearch and Redis operations are asynchronous and non-blocking

### Timing
- Large refresh intervals (24+ hours) are recommended for production
- Shorter intervals may be useful for development and testing
- Consider your user requirements when setting the refresh frequency

## Monitoring

Monitor the service through:
1. **Application logs** - All refresh operations are logged
2. **Pool timestamps** - Check `Pool.Timestamp` to see when each pool was last refreshed
3. **Service health** - The service will continue running even if individual pools fail

## Troubleshooting

### Service Not Running
- Check that `Enabled` is set to `true` in configuration
- Verify the service is registered in `Program.cs`
- Check application logs for startup messages

### Pools Not Refreshing
- Verify Algorand API connectivity
- Check pool processor registrations
- Review error logs for specific pool failures
- Ensure proper permissions for pool queries

### Performance Issues
- Increase `DelayBetweenPoolsSeconds` if hitting rate limits
- Monitor network connectivity to Algorand nodes
- Consider reducing `IntervalHours` if pools need more frequent updates