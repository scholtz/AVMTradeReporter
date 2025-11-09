# AVMTradeReporter.Models

A .NET library containing data models for the AVM Trade Reporter, providing structured representations of Algorand DEX data including pools, trades, liquidity events, and OHLC (Open, High, Low, Close) data.

## Features

- **Pool Models**: Represent DEX pool states with asset pairs, amounts, and protocol-specific data
- **Trade Models**: Capture trade transactions with timestamps, amounts, and metadata
- **Liquidity Models**: Track liquidity additions and removals
- **Aggregated Pool Models**: Summarized pool data across multiple protocols
- **OHLC Models**: Time-series data for price analysis
- **Asset Models**: Algorand asset metadata and pricing information

## Supported Protocols

- **Pact**: Pact.fi DEX protocol
- **Tiny**: TinyMan DEX protocol
- **Biatec**: Biatec DEX protocol

## Usage

```csharp
using AVMTradeReporter.Models.Data;

// Example: Working with a Pool
var pool = new Pool
{
    PoolAddress = "ABC123...",
    Protocol = "Pact",
    AssetIdA = 0, // ALGO
    AssetIdB = 31566704,
    RealAmountA = 1000.0m,
    RealAmountB = 50000.0m,
    VirtualAmountA = 1000.0m,
    VirtualAmountB = 50000.0m,
    Timestamp = DateTime.UtcNow
};

// Example: Working with a Trade
var trade = new Trade
{
    TxId = "transaction_id",
    PoolAddress = "ABC123...",
    AssetIdA = 0,
    AssetIdB = 31566704,
    AmountA = 100.0m,
    AmountB = 5000.0m,
    Timestamp = DateTime.UtcNow
};
```

## Installation

```bash
dotnet add package AVMTradeReporter.Models
```

## Dependencies

- .NET 8.0 or later
- Newtonsoft.Json (>= 13.0.3)
- System.Text.Json (>= 8.0.5)

## License

MIT License - see the [repository](https://github.com/scholtz/AVMTradeReporter) for details.

## Contributing

Contributions are welcome! Please see the main repository for contribution guidelines.