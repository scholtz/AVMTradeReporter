# Pool Auto-Enrichment Feature

## Overview

When pools are created or updated from trade or liquidity transactions, they may only contain basic data (like A, B, L values) but miss important metadata such as `approvalProgramHash`, asset IDs, fees, and other critical information. 

The pool repository now automatically detects when a pool is missing the `approvalProgramHash` field and automatically runs the appropriate pool processor to fetch the complete pool data from the blockchain.

## How It Works

### 1. Pool Updates from Trades
When `UpdatePoolFromTrade` is called:
- If creating a new pool and `approvalProgramHash` is missing ? runs pool processor
- If updating existing pool and `approvalProgramHash` is missing ? runs pool processor
- Pool processor fetches complete data including approval program hash, fees, asset IDs, etc.

### 2. Pool Updates from Liquidity
When `UpdatePoolFromLiquidity` is called:
- Same logic as trade updates
- Ensures all pools have complete metadata regardless of how they were discovered

### 3. Processor Selection
The system automatically selects the correct pool processor based on the pool's protocol:
- **Pact Protocol** ? `PactPoolProcessor`
- **Tiny Protocol** ? `TinyPoolProcessor` 
- **Biatec Protocol** ? `BiatecPoolProcessor`

### 4. Fallback Logic
If the primary processor fails (common between Pact/Tiny pools), the system tries alternative processors:
- If Tiny processor fails ? tries Pact processor and updates protocol if successful
- If Pact processor fails ? tries Tiny processor and updates protocol if successful
- This handles cases where pools are misidentified or have changed protocols

## Technical Implementation

### Key Changes

1. **PoolRepository Constructor**
   - Added `IServiceProvider` dependency to access pool processors

2. **Enhanced Update Methods**
   ```csharp
   // Check if enrichment is needed
   if (string.IsNullOrEmpty(pool.ApprovalProgramHash))
   {
       var enrichedPool = await TryEnrichPoolWithProcessor(pool, cancellationToken);
       if (enrichedPool != null)
       {
           pool = enrichedPool;
       }
   }
   ```

3. **New Helper Methods**
   - `TryEnrichPoolWithProcessor()` - Attempts to enrich pool with complete data
   - `GetPoolProcessor()` - Gets appropriate processor for protocol

### Error Handling

- **Graceful Degradation**: If pool enrichment fails, the basic pool data is still saved
- **Alternative Processors**: Tries different processors if primary fails
- **Comprehensive Logging**: All enrichment attempts are logged for debugging
- **No Blocking**: Pool enrichment happens synchronously but failures don't prevent pool updates

## Benefits

### 1. **Automatic Data Completeness**
- All pools automatically get complete metadata when possible
- No manual intervention required
- Works for both new and existing pools

### 2. **Protocol Detection**
- Automatically corrects protocol misidentification
- Handles pools that may work with multiple protocols
- Updates protocol field when alternative processor succeeds

### 3. **Improved Data Quality**
- Pools have approval program hashes for security verification
- Complete fee information for accurate calculations
- Proper asset ID mappings for trading pairs

### 4. **Backwards Compatibility**
- Existing pools without approval program hashes get enriched on next update
- No migration required
- Gradual improvement of data quality over time

## Logging

The feature provides detailed logging at different levels:

### Information Level
```
Enriching pool {poolAddress}_{poolAppId}_{protocol} using pool processor
Successfully enriched pool {poolAddress}_{poolAppId}_{protocol} with approvalProgramHash: {hash}
```

### Warning Level
```
Failed to enrich pool {poolAddress}_{poolAppId}_{protocol} using pool processor: {error}
No pool processor found for protocol {protocol}
```

### Debug Level
- Individual pool processor attempts
- Fallback processor attempts
- Success/failure details

## Configuration

No additional configuration is required. The feature uses existing pool processor registrations from the DI container.

## Monitoring

Monitor pool enrichment through:
1. **Application Logs** - All enrichment attempts are logged
2. **Pool Data Quality** - Check `ApprovalProgramHash` field population
3. **Protocol Accuracy** - Monitor protocol field corrections

## Performance Considerations

### Network Calls
- Each enrichment makes blockchain API calls
- Only happens when `approvalProgramHash` is missing
- Cached once enriched (won't re-enrich existing complete pools)

### Timing
- Enrichment happens during pool updates (synchronous)
- May slightly increase trade/liquidity processing time
- Improves overall data quality and reduces need for background refreshes

### Resource Usage
- Minimal additional memory usage
- Reduces long-term blockchain queries by ensuring complete data upfront
- One-time cost per pool for permanent data improvement