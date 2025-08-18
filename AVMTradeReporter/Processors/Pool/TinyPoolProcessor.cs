using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Model.Data.Enums;
using AVMTradeReporter.Repository;
using System.Text;

namespace AVMTradeReporter.Processors.Pool
{
    public class TinyPoolProcessor : IPoolProcessor
    {
        private IDefaultApi _algod;
        private IPoolRepository _poolRepository;
        private readonly ILogger<TinyPoolProcessor> _logger;
        private readonly IAssetRepository _assetRepository;

        public TinyPoolProcessor(
            IDefaultApi algod,
            IPoolRepository poolRepository,
            ILogger<TinyPoolProcessor> logger,
            IAssetRepository assetRepository)
        {
            _algod = algod;
            _poolRepository = poolRepository;
            _logger = logger;
            _assetRepository = assetRepository;
        }
        public static string Base64AssetAId = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_1_id"));
        public static string Base64A = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_1_reserves"));
        public static string Base64AssetBId = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_2_id"));
        public static string Base64B = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_2_reserves"));
        //pool_token_asset_id
        public static string Base64LTID = Convert.ToBase64String(Encoding.UTF8.GetBytes("pool_token_asset_id"));
        // total_fee_share
        public static string Base64FEE_BPS = Convert.ToBase64String(Encoding.UTF8.GetBytes("total_fee_share"));
        // protocol_fee_ratio
        public static string Base64Tiny_FEE_BPS = Convert.ToBase64String(Encoding.UTF8.GetBytes("protocol_fee_ratio"));
        public static string AFKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_1_protocol_fees"));
        public static string BFKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("asset_2_protocol_fees"));

        public async Task<AVMTradeReporter.Model.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);
            var app = await _algod.GetApplicationByIDAsync(appId);
            var localStateInfo = await _algod.AccountApplicationInformationAsync(address, appId, null);

            var A = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64A);
            if (A == null) throw new Exception("A is missing in local state");
            var B = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64B);
            if (B == null) throw new Exception("B is missing in local state");
            var AssetAId = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64AssetAId);
            if (AssetAId == null) throw new Exception("AssetAId is missing in local state");
            var AssetBId = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64AssetBId);
            if (AssetBId == null) throw new Exception("AssetBId is missing in local state");
            var LTID = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64LTID);
            if (LTID == null) throw new Exception("LTID is missing in local state");
            var FEE_BPS = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64FEE_BPS);
            if (FEE_BPS == null) throw new Exception("FEE_BPS is missing in local state");
            var TINY_FEE_BPS = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64Tiny_FEE_BPS);
            if (TINY_FEE_BPS == null) throw new Exception("TINY_FEE_BPS is missing in local state");
            var assetAId = AssetAId.Value.Uint;
            var assetBId = AssetBId.Value.Uint;
            var AF = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == AFKey);
            if (AF == null) throw new Exception("AF is null");
            var BF = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == BFKey);
            if (BF == null) throw new Exception("BF is null");



            var assetADecimals = (await _assetRepository.GetAssetAsync(assetAId, cancelationTokenSource.Token))?.Params?.Decimals;
            var assetBDecimals = (await _assetRepository.GetAssetAsync(assetBId, cancelationTokenSource.Token))?.Params?.Decimals;


            //app.Params.GlobalState
            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();

            var updated = false;
            if (pool == null)
            {
                pool = new AVMTradeReporter.Model.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = DEXProtocol.Tiny,
                    A = A.Value.Uint,
                    B = B.Value.Uint,
                    AssetIdLP = LTID.Value.Uint,
                    AMMType = AMMType.OldAMM,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = FEE_BPS.Value.Uint / 10000m,
                    ProtocolFeePortion = Convert.ToDecimal(TINY_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint),
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    AF = AF.Value.Uint,
                    BF = BF.Value.Uint,
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != DEXProtocol.Tiny)
                {
                    pool.Protocol = DEXProtocol.Tiny;
                    updated = true;
                }
                if (pool.A != A.Value.Uint)
                {
                    pool.A = A.Value.Uint;
                    updated = true;
                }
                if (pool.AF != AF.Value.Uint)
                {
                    pool.AF = AF.Value.Uint;
                    updated = true;
                }
                if (pool.ApprovalProgramHash != hash)
                {
                    pool.ApprovalProgramHash = hash;
                    updated = true;
                }
                if (pool.B != B.Value.Uint)
                {
                    pool.B = B.Value.Uint;
                    updated = true;
                }
                if (pool.BF != BF.Value.Uint)
                {
                    pool.BF = BF.Value.Uint;
                    updated = true;
                }
                if (pool.AssetIdLP != LTID.Value.Uint)
                {
                    pool.AssetIdLP = LTID.Value.Uint;
                    updated = true;
                }
                if (pool.LPFee != FEE_BPS.Value.Uint / 10000m)
                {
                    pool.LPFee = FEE_BPS.Value.Uint / 10000m;
                    updated = true;
                }
                if (pool.ProtocolFeePortion != Convert.ToDecimal(TINY_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint))
                {
                    pool.ProtocolFeePortion = Convert.ToDecimal(TINY_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint);
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
                if (pool.AMMType != AMMType.OldAMM)
                {
                    pool.AMMType = AMMType.OldAMM;
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
