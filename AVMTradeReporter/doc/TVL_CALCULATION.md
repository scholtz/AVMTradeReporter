# TVL (Total Value Locked) Calculation Documentation

## Overview

The AVM Trade Reporter calculates two types of TVL for assets:

1. **Real TVL** (`TVL_USD`): The actual liquid value backed by trusted tokens
2. **Total TVL** (`TotalTVLAssetInUSD`): The full pool value including both assets

## Trusted Reference Tokens

Trusted reference tokens are assets that have stable, well-established prices and high liquidity. These include:

- **ALGO** (Asset ID: 0) - Native Algorand token
- **USDC** (Asset ID: 31566704) - USD Coin stablecoin
- Other major stablecoins and tokens (see `AggregatedPoolRepository.cs` for complete list)

## Real TVL Calculation (`TVL_USD`)

### Definition
Real TVL represents the USD value of **only the trusted tokens** in pools where an asset is paired with a trusted reference.

### Formula
```
Real TVL = Σ (trusted_token_amount × trusted_token_price)
```
For each pool where the asset is paired with a trusted reference token.

### Example
Asset XYZ is in two pools:
- Pool 1: 100 XYZ + 50 USDC (USDC price = $1)
  - Real TVL contribution: 50 × $1 = **$50**
- Pool 2: 200 XYZ + 80 ALGO (ALGO price = $0.25)
  - Real TVL contribution: 80 × $0.25 = **$20**

**Total Real TVL for XYZ: $50 + $20 = $70**

### Why Real TVL?
Real TVL shows the actual liquid value that can be confidently valued in USD. It only counts established tokens, not the asset being valued itself, avoiding circular valuation issues.

## Total TVL Calculation (`TotalTVLAssetInUSD`)

### Definition
Total TVL represents the USD value of **both sides** (asset + trusted token) of all pools where an asset is paired with a trusted reference.

### Formula
```
Total TVL = Σ ((asset_amount × asset_price) + (trusted_token_amount × trusted_token_price))
```
For each pool where the asset is paired with a trusted reference token.

### Example
Using the same pools as above (assume XYZ price = $0.40):
- Pool 1: 100 XYZ + 50 USDC
  - Total TVL contribution: (100 × $0.40) + (50 × $1) = $40 + $50 = **$90**
- Pool 2: 200 XYZ + 80 ALGO
  - Total TVL contribution: (200 × $0.40) + (80 × $0.25) = $80 + $20 = **$100**

**Total TVL for XYZ: $90 + $100 = $190**

### Why Total TVL?
Total TVL shows the complete pool value and total liquidity available for the asset. This is useful for understanding the full market depth.

## Implementation Details

### Location
The calculations are implemented in:
- `AVMTradeReporter/Repository/AggregatedPoolRepository.cs`
- Method: `UpdateRelatedAssetsAsync()`

### Process Flow
1. When an aggregated pool is updated, affected assets are identified
2. For each asset:
   - Find all aggregated pools where it's paired with a trusted reference
   - For Real TVL: Sum only the trusted token side values
   - For Total TVL: Sum both sides of the pools
   - Update the asset's `TVL_USD` and `TotalTVLAssetInUSD` fields

### Deduplication
Pools are stored in both directions (A-B and B-A) in the cache. The calculation uses a `processedPairs` HashSet to ensure each unique pool pair is only counted once.

## Asset Price Calculation

Asset prices are derived from pools with trusted references:

1. **Direct USDC pair**: If asset has a direct USDC pool, price = USDC_amount / asset_amount
2. **Via ALGO**: If no USDC pool, derive price through ALGO: price = (ALGO_per_asset) × (ALGO_price_in_USD)
3. **ALGO price**: Derived from ALGO-USDC pool
4. **USDC price**: Fixed at $1.00

## Testing

Comprehensive unit tests are provided in:
- `AVMTradeReporterTests/Model/AssetPriceTvlTests.cs`

Test cases cover:
- Direct USDC pools
- Price derivation via ALGO
- Real TVL vs Total TVL validation
- Multiple pool aggregation

## Example Scenarios

### Scenario 1: Single USDC Pool
```
Pool: 1,000 TOKEN + 500 USDC
TOKEN price: $0.50
```
- **Real TVL**: 500 × $1 = **$500**
- **Total TVL**: (1,000 × $0.50) + (500 × $1) = $500 + $500 = **$1,000**

### Scenario 2: Multiple Pools
```
Pool 1: 10 TOKEN + 20 USDC
Pool 2: 8 TOKEN + 64 ALGO (ALGO = $0.25)
TOKEN price: $2.00
```
- **Real TVL**: 
  - Pool 1: 20 × $1 = $20
  - Pool 2: 64 × $0.25 = $16
  - Total: **$36**
  
- **Total TVL**:
  - Pool 1: (10 × $2) + (20 × $1) = $40
  - Pool 2: (8 × $2) + (64 × $0.25) = $32
  - Total: **$72**

## Notes

- Real TVL is always less than or equal to Total TVL
- Real TVL / 2 ≈ TVL in pools (since trusted tokens are typically ~50% of pool value at equilibrium)
- Prices and TVL are updated automatically when pools are stored or updated
- Both metrics are stored in the `BiatecAsset` model for API consumption
