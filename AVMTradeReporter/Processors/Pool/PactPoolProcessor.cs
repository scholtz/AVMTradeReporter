using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services.ScamRating;
using System.Text;

namespace AVMTradeReporter.Processors.Pool
{
    public class PactPoolProcessor : IPoolProcessor
    {
        private IDefaultApi _algod;
        private IPoolRepository _poolRepository;
        ILogger<PactPoolProcessor> _logger;
        private readonly IAssetRepository _assetRepository;

        public PactPoolProcessor(
            IDefaultApi algod,
            IPoolRepository poolRepository,
            ILogger<PactPoolProcessor> logger,
            IAssetRepository assetRepository)
        {
            _algod = algod;
            _poolRepository = poolRepository;
            _logger = logger;
            _assetRepository = assetRepository;
        }

        /// <summary>
        /// A tick based CLAMM pool read from the chain is stamped with the time the read started minus this margin, see
        /// <see cref="LoadClammPoolAsync"/>.
        /// </summary>
        public static readonly TimeSpan ReadTimestampSafetyMargin = TimeSpan.FromMinutes(1);

        // one load per pool at a time (process wide - the processor may be resolved more than once): a refresh that read the
        // tick boxes before a mint must not finish after - and overwrite - the reload triggered by that mint
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, SemaphoreSlim> _loadLocks = new();

        public async Task<AVMTradeReporter.Models.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            var loadLock = _loadLocks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
            await loadLock.WaitAsync();
            try
            {
                return await LoadPoolCoreAsync(address, appId);
            }
            finally
            {
                loadLock.Release();
            }
        }

        private async Task<AVMTradeReporter.Models.Data.Pool> LoadPoolCoreAsync(string address, ulong appId)
        {
            // before any algod read: the state read below contains at least every block committed before this instant
            var readStartedAt = DateTimeOffset.Now;
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);
            // events keep updating the cached pool while the reads below run; its event version tells whether one did
            var versionBeforeRead = pool?.EventVersion;
            var liquidityVersionBeforeRead = pool?.LiquidityVersion;
            var app = await _algod.GetApplicationByIDAsync(appId);

            if (GetGlobalState(app, PactWeightedPoolHelper.KeyReserveA) != null)
            {
                // Pact weighted pool (ARC4 contract) - completely different global state layout
                return await LoadWeightedPoolAsync(pool, address, appId, app, cancelationTokenSource.Token);
            }
            if (GetGlobalState(app, PactClammHelper.KeyCurrentPrice) != null)
            {
                // Pact CLAMM (tick based concentrated liquidity, ARC4 contract)
                return await LoadClammPoolAsync(pool, address, appId, app, readStartedAt, versionBeforeRead, liquidityVersionBeforeRead, cancelationTokenSource.Token);
            }

            var A = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("A")));
            if (A == null) throw new Exception("A is missing in global params");
            var B = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("B")));
            if (B == null) throw new Exception("B is missing in global params");
            var L = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("L")));
            if (L == null) throw new Exception("L is missing in global params");
            var LTID = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("LTID")));
            if (LTID == null) throw new Exception("LTID is missing in global params");
            var FEE_BPS = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("FEE_BPS")));
            var PACT_FEE_BPS = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("PACT_FEE_BPS")));
            var CONFIG = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("CONFIG")));
            if (CONFIG == null) throw new Exception("CONFIG is missing in global params");

            var CONTRACT_NAME = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("CONTRACT_NAME")));
            var contractName = CONTRACT_NAME != null ? Encoding.UTF8.GetString(Convert.FromBase64String(CONTRACT_NAME.Value.Bytes)) : "Pact AMM";

            var configBytes = Convert.FromBase64String(CONFIG.Value.Bytes);
            //app.Params.GlobalState
            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();

            // assetAId is first 8 bytes from CONFIG.Value.Bytes converted to ulong
            var assetAIdBytes = configBytes.Take(8).ToArray();
            // convert big endian to ulong
            var assetAId = BitConverter.ToUInt64(assetAIdBytes.Reverse().ToArray(), 0);

            var assetBIdBytes = configBytes.Skip(8).Take(8).ToArray();
            // convert big endian to ulong
            var assetBId = BitConverter.ToUInt64(assetBIdBytes.Reverse().ToArray(), 0);

            var assetADecimals = (await _assetRepository.GetAssetAsync(assetAId, cancelationTokenSource.Token))?.Params?.Decimals;
            var assetBDecimals = (await _assetRepository.GetAssetAsync(assetBId, cancelationTokenSource.Token))?.Params?.Decimals;
            var lpFee = FEE_BPS == null ? 0.003m : FEE_BPS.Value.Uint / 10000m;
            var pactFee = PACT_FEE_BPS == null || FEE_BPS == null ? 0 : Convert.ToDecimal(PACT_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint);

            var type = AMMType.OldAMM;
            var a = A.Value.Uint;
            var b = B.Value.Uint;
            ulong? stableA = null;
            ulong? stableB = null;
            ulong? amplifier = null;
            if (contractName == "[SI] PACT AMM")
            {
                type = AMMType.StableSwap;
                stableA = a;
                stableB = b;
                a = 0;
                b = 0;

                var INITIAL_A = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("INITIAL_A")));
                var FUTURE_A = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("FUTURE_A")));
                var INITIAL_A_TIME = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("INITIAL_A_TIME")));
                var FUTURE_A_TIME = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("FUTURE_A_TIME")));

                if (INITIAL_A != null && FUTURE_A != null && INITIAL_A_TIME != null && FUTURE_A_TIME != null)
                {
                    amplifier = CalculateAmplifier(INITIAL_A.Value.Uint, FUTURE_A.Value.Uint, INITIAL_A_TIME.Value.Uint, FUTURE_A_TIME.Value.Uint);
                }
            }
            _logger.LogInformation($"Processing {contractName}");

            var updated = false;
            if (pool == null)
            {
                pool = new AVMTradeReporter.Models.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = DEXProtocol.Pact,
                    A = a,
                    B = b,
                    StableA = stableA,
                    StableB = stableB,
                    Amplifier = amplifier,
                    L = L.Value.Uint,
                    AssetIdLP = LTID.Value.Uint,
                    AMMType = type,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = lpFee,
                    ProtocolFeePortion = pactFee,
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    AssetADecimals = assetADecimals,
                    AssetBDecimals = assetBDecimals,
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != DEXProtocol.Pact)
                {
                    pool.Protocol = DEXProtocol.Pact;
                    updated = true;
                }
                if (pool.A != a)
                {
                    pool.A = a;
                    updated = true;
                }
                if (pool.StableA != stableA)
                {
                    pool.StableA = stableA;
                    updated = true;
                }
                if (pool.ApprovalProgramHash != hash)
                {
                    pool.ApprovalProgramHash = hash;
                    updated = true;
                }
                if (pool.B != b)
                {
                    pool.B = b;
                    updated = true;
                }
                if (pool.StableB != stableB)
                {
                    pool.StableB = stableB;
                    updated = true;
                }
                if (pool.Amplifier != amplifier)
                {
                    pool.Amplifier = amplifier;
                    updated = true;
                }
                if (pool.L != L.Value.Uint)
                {
                    pool.L = L.Value.Uint;
                    updated = true;
                }
                if (pool.AssetIdLP != LTID.Value.Uint)
                {
                    pool.AssetIdLP = LTID.Value.Uint;
                    updated = true;
                }
                if (pool.LPFee != lpFee)
                {
                    pool.LPFee = lpFee;
                    updated = true;
                }
                if (pool.ProtocolFeePortion != pactFee)
                {
                    pool.ProtocolFeePortion = pactFee;
                    updated = true;
                }
                if (pool.AssetIdA != assetAId)
                {
                    pool.AssetIdA = assetAId;
                    updated = true;
                }
                if (pool.AssetIdB != assetBId)
                {
                    pool.AssetIdB = assetBId;
                    updated = true;
                }
                if (pool.AssetADecimals != assetADecimals)
                {
                    pool.AssetADecimals = assetADecimals;
                    updated = true;
                }
                if (pool.AssetBDecimals != assetBDecimals)
                {
                    pool.AssetBDecimals = assetBDecimals;
                    updated = true;
                }
                if (pool.AMMType != type)
                {
                    pool.AMMType = type;
                    updated = true;
                }

                if (updated)
                {
                    pool.Timestamp = DateTimeOffset.Now;
                }
            }
            if (updated)
            {
                _logger.LogInformation("Pool {appId} {appAddress} updated with pool refresh", pool.PoolAppId, pool.PoolAddress);
                await _poolRepository.StorePoolAsync(pool, true, cancelationTokenSource.Token);
            }
            return pool;
        }

        private static Algorand.Algod.Model.TealKeyValue? GetGlobalState(Algorand.Algod.Model.Application app, string key)
        {
            return app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes(key)));
        }

        /// <summary>
        /// Loads the Pact weighted pool (ARC4 contract with reserve_a / reserve_b / issued_lp / weight_a global state).
        /// See <see cref="PactWeightedPoolHelper"/> for the differences from the classic Pact AMM.
        /// </summary>
        private async Task<AVMTradeReporter.Models.Data.Pool> LoadWeightedPoolAsync(AVMTradeReporter.Models.Data.Pool? pool, string address, ulong appId, Algorand.Algod.Model.Application app, CancellationToken cancellationToken)
        {
            var reserveA = GetGlobalState(app, PactWeightedPoolHelper.KeyReserveA);
            if (reserveA == null) throw new Exception("reserve_a is missing in global params");
            var reserveB = GetGlobalState(app, PactWeightedPoolHelper.KeyReserveB);
            if (reserveB == null) throw new Exception("reserve_b is missing in global params");
            var issuedLP = GetGlobalState(app, PactWeightedPoolHelper.KeyIssuedLP);
            if (issuedLP == null) throw new Exception("issued_lp is missing in global params");
            var lpAsset = GetGlobalState(app, "lp_asset");
            if (lpAsset == null) throw new Exception("lp_asset is missing in global params");
            var assetA = GetGlobalState(app, "asset_a");
            if (assetA == null) throw new Exception("asset_a is missing in global params");
            var assetB = GetGlobalState(app, "asset_b");
            if (assetB == null) throw new Exception("asset_b is missing in global params");
            var swapFeeBps = GetGlobalState(app, "swap_fee_bps");
            var protocolFeeBps = GetGlobalState(app, "protocol_fee_bps");
            var managerFeeBps = GetGlobalState(app, "manager_fee_bps");
            var weightABps = GetGlobalState(app, "weight_a");

            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();
            var assetAId = assetA.Value.Uint;
            var assetBId = assetB.Value.Uint;
            var assetADecimals = (await _assetRepository.GetAssetAsync(assetAId, cancellationToken))?.Params?.Decimals;
            var assetBDecimals = (await _assetRepository.GetAssetAsync(assetBId, cancellationToken))?.Params?.Decimals;

            // swap_fee_bps is the total fee taken from the swap (e.g. 10 = 0.1%)
            var lpFee = swapFeeBps == null ? 0.003m : swapFeeBps.Value.Uint / 10000m;
            // protocol_fee_bps and manager_fee_bps are the portions of the swap fee (in bps of the fee) not going to liquidity providers (e.g. 2500 = 25%)
            var protocolFeePortion = ((protocolFeeBps?.Value.Uint ?? 0) + (managerFeeBps?.Value.Uint ?? 0)) / 10000m;
            // weight_a is in bps (5000 = 50%)
            var weightA = weightABps == null ? 0.5m : weightABps.Value.Uint / 10000m;
            var weightB = 1 - weightA;

            _logger.LogInformation("Processing Pact weighted pool {appId}", appId);

            var updated = false;
            if (pool == null)
            {
                pool = new AVMTradeReporter.Models.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = DEXProtocol.Pact,
                    A = reserveA.Value.Uint,
                    B = reserveB.Value.Uint,
                    L = issuedLP.Value.Uint,
                    AssetIdLP = lpAsset.Value.Uint,
                    AMMType = AMMType.WeightedAMM,
                    WeightA = weightA,
                    WeightB = weightB,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = lpFee,
                    ProtocolFeePortion = protocolFeePortion,
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    AssetADecimals = assetADecimals,
                    AssetBDecimals = assetBDecimals,
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != DEXProtocol.Pact) { pool.Protocol = DEXProtocol.Pact; updated = true; }
                if (pool.A != reserveA.Value.Uint) { pool.A = reserveA.Value.Uint; updated = true; }
                if (pool.B != reserveB.Value.Uint) { pool.B = reserveB.Value.Uint; updated = true; }
                if (pool.StableA != null) { pool.StableA = null; updated = true; }
                if (pool.StableB != null) { pool.StableB = null; updated = true; }
                if (pool.Amplifier != null) { pool.Amplifier = null; updated = true; }
                if (pool.L != issuedLP.Value.Uint) { pool.L = issuedLP.Value.Uint; updated = true; }
                if (pool.AssetIdLP != lpAsset.Value.Uint) { pool.AssetIdLP = lpAsset.Value.Uint; updated = true; }
                if (pool.ApprovalProgramHash != hash) { pool.ApprovalProgramHash = hash; updated = true; }
                if (pool.LPFee != lpFee) { pool.LPFee = lpFee; updated = true; }
                if (pool.ProtocolFeePortion != protocolFeePortion) { pool.ProtocolFeePortion = protocolFeePortion; updated = true; }
                if (pool.AssetIdA != assetAId) { pool.AssetIdA = assetAId; updated = true; }
                if (pool.AssetIdB != assetBId) { pool.AssetIdB = assetBId; updated = true; }
                if (pool.AssetADecimals != assetADecimals) { pool.AssetADecimals = assetADecimals; updated = true; }
                if (pool.AssetBDecimals != assetBDecimals) { pool.AssetBDecimals = assetBDecimals; updated = true; }
                if (pool.AMMType != AMMType.WeightedAMM) { pool.AMMType = AMMType.WeightedAMM; updated = true; }
                if (pool.WeightA != weightA) { pool.WeightA = weightA; updated = true; }
                if (pool.WeightB != weightB) { pool.WeightB = weightB; updated = true; }
                if (updated)
                {
                    pool.Timestamp = DateTimeOffset.Now;
                }
            }
            if (updated)
            {
                _logger.LogInformation("Pool {appId} {appAddress} updated with pool refresh", pool.PoolAppId, pool.PoolAddress);
                await _poolRepository.StorePoolAsync(pool, true, cancellationToken);
            }
            return pool;
        }

        /// <summary>
        /// Loads the Pact CLAMM pool (tick based concentrated liquidity). See <see cref="PactClammHelper"/> for the layout.
        /// The reserves are derived from the liquidity ranges read from the tick boxes.
        ///
        /// The pool is stamped with <paramref name="readStartedAt"/> minus <see cref="ReadTimestampSafetyMargin"/> (never
        /// moving an existing timestamp backwards): the timestamp guards of the pool repository skip the events of older
        /// blocks, which the read state already contains, while events of blocks near the read - possibly not contained in
        /// it, given the time the reads take and block timestamp skew - are applied rather than skipped. Re-applying one is
        /// harmless (a liquidity event reloads again, a swap reprices from its own post-swap price), skipping a liquidity
        /// event the read missed would leave stale ranges.
        /// </summary>
        private async Task<AVMTradeReporter.Models.Data.Pool> LoadClammPoolAsync(AVMTradeReporter.Models.Data.Pool? pool, string address, ulong appId, Algorand.Algod.Model.Application app, DateTimeOffset readStartedAt, long? versionBeforeRead, long? liquidityVersionBeforeRead, CancellationToken cancellationToken)
        {
            var stateTimestamp = readStartedAt - ReadTimestampSafetyMargin;
            var assetA = GetGlobalState(app, "a");
            if (assetA == null) throw new Exception("a (asset A id) is missing in global params");
            var assetB = GetGlobalState(app, "b");
            if (assetB == null) throw new Exception("b (asset B id) is missing in global params");
            var currentPrice = GetGlobalState(app, PactClammHelper.KeyCurrentPrice);
            if (currentPrice == null) throw new Exception("current_price is missing in global params");
            var currentTick = GetGlobalState(app, PactClammHelper.KeyCurrentTick);
            var tickSpacing = GetGlobalState(app, "tick_spacing");
            // "d": which side of price 1 the current price is on, decides the orientation of current_price (see TickMath)
            var tickOriginState = GetGlobalState(app, PactClammHelper.KeyTickOrigin);
            var feeBps = GetGlobalState(app, "fee_bps");
            var protocolFeeBps = GetGlobalState(app, "protocol_fee_bps");

            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();
            var assetAId = assetA.Value.Uint;
            var assetBId = assetB.Value.Uint;
            var assetADecimals = (await _assetRepository.GetAssetAsync(assetAId, cancellationToken))?.Params?.Decimals;
            var assetBDecimals = (await _assetRepository.GetAssetAsync(assetBId, cancellationToken))?.Params?.Decimals;

            var lpFee = feeBps == null ? 0.003m : feeBps.Value.Uint / 10000m;
            // protocol_fee_bps is the portion of the swap fee (in bps of the fee) taken by the protocol (2000 = 20%)
            var protocolFeePortion = (protocolFeeBps?.Value.Uint ?? 0) / 10000m;
            // scale is the fixed point denominator of current_price / low_price / high_price (2^64 - 1 => Q64.64)
            var scale = GetGlobalState(app, "scale");
            if (scale != null && scale.Value.Uint != ulong.MaxValue)
            {
                _logger.LogWarning("Pact CLAMM pool {appId} uses unexpected price scale {scale}; price conversion assumes Q64.64", appId, scale.Value.Uint);
            }
            ulong origin = tickOriginState?.Value.Uint ?? TickMath.TickBase;
            var price = PactClammHelper.SqrtPriceX64ToPrice(currentPrice.Value.Uint, assetADecimals ?? 0, assetBDecimals ?? 0, origin);
            ulong? tick = currentTick?.Value.Uint;
            ulong? spacing = tickSpacing?.Value.Uint;

            // reserves are not in the global state: split the pool into its liquidity ranges (contiguous ticks of equal
            // liquidity, read from the tick word boxes) and sum what every range holds at the current sqrt price.
            // The ranges are kept on the pool so every later swap can recompute the reserves exactly from its post-swap
            // price. When the boxes cannot be read the stored ranges are kept if there are any and repriced - every indexed
            // liquidity event drops them (PoolReserveUpdater.ApplyLiquidity), so they are as current as the event tracking
            // could make the reserves; without them the stored (event tracked) reserves are kept.
            ulong reserveA = pool?.A ?? 0;
            ulong reserveB = pool?.B ?? 0;
            ulong activeLiquidity = pool?.L ?? 0;
            List<PoolLiquidityRange>? ranges = null;
            var freshRanges = false;
            try
            {
                var ticks = await LoadClammTicksAsync(appId);
                ranges = TickMath.BuildRanges(ticks);
                freshRanges = true;
            }
            catch (Exception ex)
            {
                ranges = pool?.LiquidityRanges == null ? null : TickMath.CloneRanges(pool.LiquidityRanges);
                _logger.LogWarning(ex, "Failed to read the tick boxes of Pact CLAMM pool {appId}, {fallback}", appId,
                    ranges == null ? "keeping the event tracked reserves" : "repricing the stored liquidity ranges");
            }
            if (ranges != null)
            {
                (reserveA, reserveB, activeLiquidity) = TickMath.ApplyPrice(ranges, currentPrice.Value.Uint, spacing ?? 1, origin, assetADecimals ?? 0, assetBDecimals ?? 0);
                if (tick.HasValue)
                {
                    // the contract's current tick decides the active liquidity (exact also on a tick boundary)
                    activeLiquidity = TickMath.LiquidityAtTick(ranges, tick.Value);
                }
            }

            _logger.LogInformation("Processing Pact CLAMM pool {appId}", appId);

            if (pool != null && ScamRatingPolicy.ShouldZeroBalances(pool.ScamRating))
            {
                // a scam-zeroed pool never keeps ranges (ScamRatingPolicy.ApplyBalanceRule): keep them out of the comparison
                // too, or every refresh would count as a change
                ranges = null;
            }
            if (pool != null && pool.LiquidityVersion != liquidityVersionBeforeRead)
            {
                // a liquidity event was applied while the chain was being read: the read ranges may predate it. Keep the
                // event state - the event queued its own reload behind the load lock, which reads the boxes again.
                _logger.LogInformation("Pact CLAMM pool {appId} got a liquidity event during the read, keeping the event state", appId);
                return pool;
            }
            if (pool != null && pool.EventVersion != versionBeforeRead)
            {
                // only swaps were applied meanwhile: their price / tick / origin are newer than the read and stay, but a
                // swap never changes the per-tick liquidity, so the freshly read ranges are valid - reprice them at the
                // pool's own (event) price
                if (freshRanges && ranges != null && pool.SqrtPriceX64.HasValue)
                {
                    // a pool stored before the origin was tracked: the swaps did not cross price 1 (that sets it), so the read one holds
                    pool.TickOrigin ??= origin;
                    pool.LiquidityRanges = ranges;
                    PoolReserveUpdater.ApplyRanges(pool, pool.SqrtPriceX64.Value);
                    await _poolRepository.StorePoolAsync(pool, true, cancellationToken);
                }
                return pool;
            }

            var updated = false;
            if (pool == null)
            {
                pool = new AVMTradeReporter.Models.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = DEXProtocol.Pact,
                    A = reserveA,
                    B = reserveB,
                    L = activeLiquidity,
                    AssetIdLP = 0,
                    AMMType = AMMType.TickBasedCLAMM,
                    CurrentPrice = price,
                    CurrentTick = tick,
                    TickSpacing = spacing,
                    TickOrigin = origin,
                    SqrtPriceX64 = currentPrice.Value.Uint,
                    LiquidityRanges = ranges,
                    Timestamp = stateTimestamp,
                    ApprovalProgramHash = hash,
                    LPFee = lpFee,
                    ProtocolFeePortion = protocolFeePortion,
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    AssetADecimals = assetADecimals,
                    AssetBDecimals = assetBDecimals,
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != DEXProtocol.Pact) { pool.Protocol = DEXProtocol.Pact; updated = true; }
                if (pool.A != reserveA) { pool.A = reserveA; updated = true; }
                if (pool.B != reserveB) { pool.B = reserveB; updated = true; }
                if (pool.L != activeLiquidity) { pool.L = activeLiquidity; updated = true; }
                if (pool.StableA != null) { pool.StableA = null; updated = true; }
                if (pool.StableB != null) { pool.StableB = null; updated = true; }
                if (pool.Amplifier != null) { pool.Amplifier = null; updated = true; }
                if (pool.ApprovalProgramHash != hash) { pool.ApprovalProgramHash = hash; updated = true; }
                if (pool.LPFee != lpFee) { pool.LPFee = lpFee; updated = true; }
                if (pool.ProtocolFeePortion != protocolFeePortion) { pool.ProtocolFeePortion = protocolFeePortion; updated = true; }
                if (pool.AssetIdA != assetAId) { pool.AssetIdA = assetAId; updated = true; }
                if (pool.AssetIdB != assetBId) { pool.AssetIdB = assetBId; updated = true; }
                if (pool.AssetADecimals != assetADecimals) { pool.AssetADecimals = assetADecimals; updated = true; }
                if (pool.AssetBDecimals != assetBDecimals) { pool.AssetBDecimals = assetBDecimals; updated = true; }
                if (pool.AMMType != AMMType.TickBasedCLAMM) { pool.AMMType = AMMType.TickBasedCLAMM; updated = true; }
                if (pool.CurrentPrice != price) { pool.CurrentPrice = price; updated = true; }
                if (pool.CurrentTick != tick) { pool.CurrentTick = tick; updated = true; }
                if (pool.TickSpacing != spacing) { pool.TickSpacing = spacing; updated = true; }
                if (pool.TickOrigin != origin) { pool.TickOrigin = origin; updated = true; }
                if (pool.SqrtPriceX64 != currentPrice.Value.Uint) { pool.SqrtPriceX64 = currentPrice.Value.Uint; updated = true; }
                if (!TickMath.SameRanges(pool.LiquidityRanges, ranges)) { updated = true; }
                // always take the freshly priced ranges: their amounts follow the current price even when the bounds did not change
                pool.LiquidityRanges = ranges;
                if (updated)
                {
                    pool.Timestamp = pool.Timestamp.HasValue && pool.Timestamp.Value > stateTimestamp ? pool.Timestamp : stateTimestamp;
                }
            }
            if (updated)
            {
                _logger.LogInformation("Pool {appId} {appAddress} updated with pool refresh", pool.PoolAppId, pool.PoolAddress);
                await _poolRepository.StorePoolAsync(pool, true, cancellationToken);
            }
            return pool;
        }

        /// <summary>
        /// Reads the per-tick liquidity of the Pact CLAMM pool from its tick word boxes (8 byte box names).
        /// Both tick regions are read (words below and above 1e6, see TickMath); position ('u' + address + word) and the
        /// 'l' + word boxes are skipped by their name length.
        /// </summary>
        private async Task<List<(ulong Tick, ulong Liquidity)>> LoadClammTicksAsync(ulong appId)
        {
            var result = new List<(ulong Tick, ulong Liquidity)>();
            var boxes = await _algod.GetApplicationBoxesAsync(appId, null);
            // a missing listing or box value is a failed read, never "no liquidity": a partial range list would be taken as exact
            if (boxes?.Boxes == null) throw new InvalidOperationException($"algod returned no box listing for app {appId}");
            var words = boxes.Boxes
                .Where(d => d.Name != null && d.Name.Length == 8)
                .Select(d => (Name: d.Name, Word: BitConverter.ToUInt64(d.Name.Reverse().ToArray(), 0)))
                .ToList();
            // the word boxes are independent - read them concurrently (a handful per pool, bounded to stay polite to algod)
            using var throttle = new SemaphoreSlim(8);
            var reads = words.Select(async w =>
            {
                await throttle.WaitAsync();
                try
                {
                    var box = await _algod.GetApplicationBoxByNameAsync(appId, "b64:" + Convert.ToBase64String(w.Name));
                    if (box?.Value == null) throw new InvalidOperationException($"algod returned no value for tick word {w.Word} of app {appId}");
                    return TickMath.ParseTickWord(w.Word, box.Value).ToList();
                }
                finally
                {
                    throttle.Release();
                }
            });
            foreach (var ticks in await Task.WhenAll(reads))
            {
                result.AddRange(ticks);
            }
            return result;
        }

        private ulong CalculateAmplifier(ulong initialA, ulong futureA, ulong initialATime, ulong futureATime)
        {
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now < futureATime)
            {
                var futureInitialTimeDiff = futureATime - initialATime;
                var currentInitialTimeDiff = now - initialATime;
                if (futureA > initialA)
                {
                    return initialA + (futureA - initialA) * currentInitialTimeDiff / futureInitialTimeDiff;
                }
                else
                {
                    return initialA - (initialA - futureA) * currentInitialTimeDiff / futureInitialTimeDiff;
                }
            }
            return futureA;
        }
    }
}
