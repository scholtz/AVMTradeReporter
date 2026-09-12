using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
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

        public async Task<AVMTradeReporter.Models.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);
            var app = await _algod.GetApplicationByIDAsync(appId);

            if (GetGlobalState(app, PactWeightedPoolHelper.KeyReserveA) != null)
            {
                // Pact weighted pool (ARC4 contract) - completely different global state layout
                return await LoadWeightedPoolAsync(pool, address, appId, app, cancelationTokenSource.Token);
            }
            if (GetGlobalState(app, PactClammHelper.KeyCurrentPrice) != null)
            {
                // Pact CLAMM (tick based concentrated liquidity, ARC4 contract)
                return await LoadClammPoolAsync(pool, address, appId, app, cancelationTokenSource.Token);
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
        /// The contract does not expose reserves, so A / B are kept from the stored pool and updated incrementally from events.
        /// </summary>
        private async Task<AVMTradeReporter.Models.Data.Pool> LoadClammPoolAsync(AVMTradeReporter.Models.Data.Pool? pool, string address, ulong appId, Algorand.Algod.Model.Application app, CancellationToken cancellationToken)
        {
            var assetA = GetGlobalState(app, "a");
            if (assetA == null) throw new Exception("a (asset A id) is missing in global params");
            var assetB = GetGlobalState(app, "b");
            if (assetB == null) throw new Exception("b (asset B id) is missing in global params");
            var currentPrice = GetGlobalState(app, PactClammHelper.KeyCurrentPrice);
            if (currentPrice == null) throw new Exception("current_price is missing in global params");
            var currentTick = GetGlobalState(app, PactClammHelper.KeyCurrentTick);
            var tickSpacing = GetGlobalState(app, "tick_spacing");
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
            var price = PactClammHelper.SqrtPriceX64ToPrice(currentPrice.Value.Uint, assetADecimals ?? 0, assetBDecimals ?? 0);
            ulong? tick = currentTick?.Value.Uint;
            ulong? spacing = tickSpacing?.Value.Uint;

            // reserves are not in the global state: sum the liquidity of every tick (stored in the tick word boxes)
            // at the current sqrt price. Falls back to the stored (event tracked) reserves when the boxes cannot be read.
            ulong reserveA = pool?.A ?? 0;
            ulong reserveB = pool?.B ?? 0;
            ulong activeLiquidity = pool?.L ?? 0;
            try
            {
                var ticks = await LoadClammTicksAsync(appId);
                (reserveA, reserveB) = TickMath.ComputeReserves(ticks, currentPrice.Value.Uint, spacing ?? 1);
                activeLiquidity = tick.HasValue ? TickMath.ActiveLiquidity(ticks, tick.Value) : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute reserves of Pact CLAMM pool {appId} from tick boxes, keeping stored reserves", appId);
            }

            _logger.LogInformation("Processing Pact CLAMM pool {appId}", appId);

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
        /// Reads the per-tick liquidity of the Pact CLAMM pool from its tick word boxes (8 byte box names).
        /// Position ('u' + address + word) and other boxes are skipped.
        /// </summary>
        private async Task<List<(ulong Tick, ulong Liquidity)>> LoadClammTicksAsync(ulong appId)
        {
            var result = new List<(ulong Tick, ulong Liquidity)>();
            var boxes = await _algod.GetApplicationBoxesAsync(appId, null);
            if (boxes?.Boxes == null) return result;
            foreach (var descriptor in boxes.Boxes)
            {
                if (descriptor.Name == null || descriptor.Name.Length != 8) continue;
                var word = BitConverter.ToUInt64(descriptor.Name.Reverse().ToArray(), 0);
                if (word < TickMath.TickBase) continue;
                var box = await _algod.GetApplicationBoxByNameAsync(appId, "b64:" + Convert.ToBase64String(descriptor.Name));
                if (box?.Value == null) continue;
                result.AddRange(TickMath.ParseTickWord(word, box.Value));
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
