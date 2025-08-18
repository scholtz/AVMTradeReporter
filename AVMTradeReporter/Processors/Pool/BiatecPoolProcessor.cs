using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Repository;
using System.Text;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace AVMTradeReporter.Processors.Pool
{
    public class BiatecPoolProcessor : IPoolProcessor
    {
        private IDefaultApi _algod;
        private IPoolRepository _poolRepository;
        private readonly ILogger<BiatecPoolProcessor> _logger;
        private readonly IAssetRepository _assetRepository;
        public BiatecPoolProcessor(
            IDefaultApi algod,
            IPoolRepository poolRepository,
            ILogger<BiatecPoolProcessor> logger,
             IAssetRepository assetRepository)
        {
            _algod = algod;
            _poolRepository = poolRepository;
            _logger = logger;
            _assetRepository = assetRepository;
        }

        public static string Base64ConfigProvider = Convert.ToBase64String(Encoding.UTF8.GetBytes("bc"));
        public static string Base64AssetAId = Convert.ToBase64String(Encoding.UTF8.GetBytes("a"));
        public static string Base64AssetABalance = Convert.ToBase64String(Encoding.UTF8.GetBytes("ab"));
        public static string Base64AssetBId = Convert.ToBase64String(Encoding.UTF8.GetBytes("b"));
        public static string Base64AssetBBalance = Convert.ToBase64String(Encoding.UTF8.GetBytes("bb"));
        public static string Base64AssetLPId = Convert.ToBase64String(Encoding.UTF8.GetBytes("lp"));
        public static string Base64AssetL = Convert.ToBase64String(Encoding.UTF8.GetBytes("L"));
        public static string Base64FeeScale = Convert.ToBase64String(Encoding.UTF8.GetBytes("f"));
        public static string Base64VerificationClass = Convert.ToBase64String(Encoding.UTF8.GetBytes("c"));
        public static string Base64Scale = Convert.ToBase64String(Encoding.UTF8.GetBytes("scale"));
        public static string BasepMin = Convert.ToBase64String(Encoding.UTF8.GetBytes("pMin"));
        public static string BasepMax = Convert.ToBase64String(Encoding.UTF8.GetBytes("pMax"));

        public async Task<AVMTradeReporter.Model.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);

            var app = await _algod.GetApplicationByIDAsync(appId);

            var config = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64ConfigProvider);
            if (config == null) throw new Exception("Config is missing in global params");
            var configApp = await _algod.GetApplicationByIDAsync(config.Value.Uint);
            if (configApp == null) throw new Exception("Config application not found");

            var SCALE = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64Scale);
            if (SCALE == null) throw new Exception("SCALE is missing in config application");
            var assetA = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetAId);
            if (assetA == null) throw new Exception("Asset A is missing in config application");
            var A = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetABalance);
            if (A == null) throw new Exception("Asset A balance is missing in config application");
            var assetB = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetBId);
            if (assetB == null) throw new Exception("Asset B is missing in config application");
            var B = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetBBalance);
            if (B == null) throw new Exception("Asset B balance is missing in config application");
            var LTID = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetLPId);
            if (LTID == null) throw new Exception("LP Asset ID is missing in config application");
            var L = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64AssetL);
            if (L == null) throw new Exception("L is missing in config application");
            var FEE_SCALE = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64FeeScale);
            if (FEE_SCALE == null) throw new Exception("FEE_SCALE is missing in config application");

            var FEE_Protocol = configApp.Params.GlobalState.FirstOrDefault(p => p.Key == Base64FeeScale);
            if (FEE_Protocol == null) throw new Exception("FEE_Protocol is missing in config application");
            var FEE_Protocol_Scale = 1_000_000_000m;

            var VerificationClass = app.Params.GlobalState.FirstOrDefault(p => p.Key == Base64VerificationClass);
            if (VerificationClass == null) throw new Exception("VerificationClass is missing in config application");

            var pMin = app.Params.GlobalState.FirstOrDefault(p => p.Key == BasepMin);
            if (pMin == null) throw new Exception("pMin is missing in config application");

            var pMax = app.Params.GlobalState.FirstOrDefault(p => p.Key == BasepMax);
            if (pMax == null) throw new Exception("pMax is missing in config application");


            var assetAId = assetA.Value.Uint;
            var assetBId = assetB.Value.Uint;
            var lpFee = FEE_SCALE.Value.Uint / SCALE.Value.Uint;
            var protocolFeePortion = Convert.ToDecimal(FEE_Protocol.Value.Uint) / FEE_Protocol_Scale;
            //app.Params.GlobalState
            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();

            var updated = false;
            var scaleDecimal = Convert.ToDecimal(SCALE.Value.Uint);
            var pMaxValue = Convert.ToDecimal(pMax.Value.Uint) / scaleDecimal;
            var pMinValue = Convert.ToDecimal(pMin.Value.Uint) / scaleDecimal;

            var assetADecimals = (await _assetRepository.GetAssetAsync(assetAId, cancelationTokenSource.Token))?.Params?.Decimals;
            var assetBDecimals = (await _assetRepository.GetAssetAsync(assetBId, cancelationTokenSource.Token))?.Params?.Decimals;

            var aBytes = Convert.FromBase64String(A.Value.Bytes);
            var a = BitConverter.ToUInt64(aBytes.Skip(24).Take(8).Reverse().ToArray(), 0);
            var bBytes = Convert.FromBase64String(B.Value.Bytes);
            var b = BitConverter.ToUInt64(bBytes.Skip(24).Take(8).Reverse().ToArray(), 0);
            var lBytes = Convert.FromBase64String(L.Value.Bytes);
            var l = BitConverter.ToUInt64(lBytes.Skip(24).Take(8).Reverse().ToArray(), 0);


            if (pool == null)
            {
                pool = new AVMTradeReporter.Model.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = AVMTradeReporter.Model.Data.DEXProtocol.Biatec,
                    A = a,
                    B = b,
                    L = l,
                    AssetIdLP = LTID.Value.Uint,
                    AMMType = Model.Data.AMMType.ConcentratedLiquidityAMM,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = lpFee,
                    ProtocolFeePortion = protocolFeePortion,
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    PMax = pMaxValue,
                    PMin = pMinValue,
                    AssetADecimals = assetADecimals,
                    AssetBDecimals = assetBDecimals,
                    VerificationClass = VerificationClass.Value.Uint
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != Model.Data.DEXProtocol.Biatec)
                {
                    pool.Protocol = Model.Data.DEXProtocol.Biatec;
                    updated = true;
                }
                if (pool.A != a)
                {
                    pool.A = a;
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
                if (pool.L != l)
                {
                    pool.L = l;
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
                if (pool.ProtocolFeePortion != protocolFeePortion)
                {
                    pool.ProtocolFeePortion = protocolFeePortion;
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

                if (pool.PMax != pMaxValue)
                {
                    pool.PMax = pMaxValue;
                    updated = true;
                }
                if (pool.PMin != pMinValue)
                {
                    pool.PMin = pMinValue;
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

                if (pool.VerificationClass != VerificationClass.Value.Uint)
                {
                    pool.VerificationClass = VerificationClass.Value.Uint;
                    updated = true;
                }

                if (pool.AMMType != Model.Data.AMMType.ConcentratedLiquidityAMM)
                {
                    pool.AMMType = Model.Data.AMMType.ConcentratedLiquidityAMM;
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
                await _poolRepository.StorePoolAsync(pool, cancelationTokenSource.Token);
            }
            return pool;
        }
    }
}
