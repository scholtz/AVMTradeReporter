using Algorand.Algod;
using AVMTradeReporter.Extensions;
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
            if (contractName == "[SI] PACT AMM")
            {
                type = AMMType.StableSwap;
                stableA = a;
                stableB = b;
                a = 0;
                b = 0;
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
                if (pool.AMMType != AMMType.OldAMM)
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
    }
}
