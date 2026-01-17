# AVM Trade Reporter

**ALWAYS** reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the information here.

## Working Effectively

### Prerequisites and Setup
- Ensure .NET 8 SDK is installed: `dotnet --version` (should show 8.0.x)
- Install dependencies: `dotnet restore` -- takes ~20 seconds. **NEVER CANCEL**.
- Build the solution: `dotnet build` -- takes ~12 seconds. **NEVER CANCEL**.
- Test the solution: `dotnet test` -- takes ~7 seconds total. **NEVER CANCEL**.

### Building and Running
- **ALWAYS** restore packages first: `dotnet restore`
- Build the project: `dotnet build`
- **Build warnings are normal** -- there are 6 nullable reference warnings in SearchService.cs that are expected
- Run the application: `cd AVMTradeReporter && dotnet run`
- Access Swagger UI at: `http://localhost:5135/swagger`
- **Application runs successfully without external services** but logs warnings about missing Elasticsearch/Algod connections

### Configuration Requirements
- **CRITICAL**: The application requires specific configuration in `appsettings.Development.json`
- **Redis can be disabled** by setting `"AppConfiguration": {"Redis": {"Enabled": false}}`
- **CORS configuration is mandatory** -- application will fail startup without it
- **Elasticsearch and Algod connections are optional for basic development** but will log errors
- Use `appsettings.example.json` as a template for configuration

### Testing
- Run all tests: `dotnet test` -- **33 tests fail due to network requirements, 12 pass offline**
- **Network-dependent tests are expected to fail** in isolated environments
- Key offline tests that should pass:
  - `ClAMMTest` (Pool virtual amount calculations)
  - `GetIntervalBuckets_*` tests (OHLC repository tests)
  - `FromPools_*` tests (Pool aggregation tests)
- **Do not attempt to fix network-dependent test failures** -- they require live Algorand node access

### Docker Support
- Docker build available but **requires internet access for NuGet packages**
- Build command: `docker build -f AVMTradeReporter/Dockerfile . -t avm-trade-reporter`
- **Docker builds will fail in network-restricted environments** -- this is expected

## Validation Scenarios

### Basic Development Validation
1. **Build validation**: Run `dotnet restore && dotnet build` and verify success with expected warnings
2. **Application startup**: Run `cd AVMTradeReporter && dotnet run` and verify it starts on port 5135
3. **API accessibility**: Navigate to `http://localhost:5135/swagger` and verify Swagger UI loads
4. **Core functionality**: Execute offline unit tests with `dotnet test` and verify 12 tests pass

### Manual Testing Scenarios
- **Swagger UI**: Verify the API documentation loads and displays all endpoints
- **Health check**: Test `/api/signalr/auth-test` endpoint (should work without authentication)
- **Configuration validation**: Verify application logs show CORS and authentication config loading

## Common Tasks

### Development Workflow
- Always run `dotnet restore && dotnet build` before making changes
- Test changes with `dotnet test` to ensure core functionality works
- Use `dotnet run --no-build` for faster iterations after building
- Access Swagger at `http://localhost:5135/swagger` for API testing

### Troubleshooting
- **Redis connection errors**: Disable Redis with `"Redis": {"Enabled": false}` in appsettings.Development.json
- **Elasticsearch errors**: Expected in development -- application continues to run
- **Network test failures**: Expected -- 33 out of 45 tests require live Algorand node access
- **CORS startup failures**: Ensure CORS origins are configured in appsettings.json
- **Build warnings**: 6 nullable reference warnings in SearchService.cs are expected and safe to ignore

### Project Structure
```
/
├── AVMTradeReporter/              # Main Web API project (.NET 8)
│   ├── Controllers/               # API controllers
│   ├── Services/                  # Background services and business logic
│   ├── Repository/                # Data access layer (Elasticsearch, Redis)
│   ├── Processors/Pool/           # Protocol-specific processors (Pact, Tiny, Biatec)
│   ├── Model/                     # Data models and DTOs
│   ├── Hubs/                      # SignalR hubs
│   ├── doc/                       # API documentation
│   └── appsettings*.json          # Configuration files
├── AVMTradeReporterTests/         # NUnit test project
│   ├── Model/                     # Model tests (offline compatible)
│   ├── Repository/                # Repository tests (offline compatible)
│   ├── Processors/                # Processor tests (require network)
│   ├── Image/                     # Image processing tests (require network)
│   └── Data/                      # Test data files
└── .github/workflows/             # CI/CD pipelines
```

### Key Configuration Files
- `appsettings.json`: Production configuration
- `appsettings.Development.json`: Development overrides
- `appsettings.example.json`: Configuration template
- `launchSettings.json`: Development server settings

### External Dependencies
- **Elasticsearch**: Document storage and search (optional for basic development)
- **Redis**: Caching layer (optional, can be disabled)
- **Algorand nodes**: Blockchain data source (required for full functionality)
- **NuGet packages**: Restored automatically with `dotnet restore`

## Timing Expectations

### Build Commands
- `dotnet restore`: ~20 seconds -- **NEVER CANCEL**, set timeout to 60+ seconds
- `dotnet build`: ~12 seconds -- **NEVER CANCEL**, set timeout to 30+ seconds  
- `dotnet test`: ~7 seconds -- **NEVER CANCEL**, set timeout to 30+ seconds
- Application startup: ~3 seconds to ready state

### Expected Behavior
- **Build succeeds with 6 warnings** (nullable reference warnings in SearchService.cs)
- **Application starts successfully** even without external service connections
- **12 out of 45 tests pass** without network connectivity
- **Docker builds require internet access** for NuGet package restoration

## Architecture Notes

This is an ASP.NET Core (.NET 8) Web API + SignalR service that:
- Processes Algorand DEX activity (trades, liquidity, pools, blocks)
- Supports multiple DEX protocols: Pact, Tiny, Biatec
- Provides real-time updates via SignalR hub at `/biatecScanHub`
- Stores data in Elasticsearch with optional Redis caching
- Includes comprehensive Swagger API documentation
- Uses AlgorandAuthentication for API security

### USD valuation (charting)

- **Trades and liquidity updates must carry USD valuation** (`Trade.ValueUSD`, `Trade.PriceUSD`, `Trade.FeesUSD`, `Liquidity.ValueUSD`) computed using cached asset prices (`BiatecAsset.PriceUSD`).
- **OHLC is stored as two series**:
  - Asset valuation (`InUSDValuation == false` or missing field for legacy docs)
  - USD valuation (`InUSDValuation == true`)
- When querying OHLC, the API can select USD vs asset series; when a document loaded from Elasticsearch does not contain `InUSDValuation`, it is treated as **asset valuation**.

The application can run in development mode without external services but will log connection errors. For full functionality, configure Elasticsearch, Redis, and Algorand node endpoints in appsettings.Development.json.