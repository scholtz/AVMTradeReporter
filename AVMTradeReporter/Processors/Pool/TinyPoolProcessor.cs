using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Repository;
using System.Text;

namespace AVMTradeReporter.Processors.Pool
{
    public class TinyPoolProcessor : IPoolProcessor
    {
        private IDefaultApi _algod;
        private IPoolRepository _poolRepository;
        public TinyPoolProcessor(IDefaultApi algod, IPoolRepository poolRepository)
        {
            _algod = algod;
            _poolRepository = poolRepository;
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

        public async Task<AVMTradeReporter.Model.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);
            var app  = await _algod.GetApplicationByIDAsync(appId);
            var localStateInfo = await _algod.AccountApplicationInformationAsync(address, appId, null);

            var A = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64A);
            if(A == null) throw new Exception("A is missing in local state");
            var B = localStateInfo.AppLocalState.KeyValue.FirstOrDefault(kv => kv.Key == Base64B);
            if(B == null) throw new Exception("B is missing in local state");
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




            //app.Params.GlobalState
            var hash = app.Params.ApprovalProgram.Bytes.ToSha256Hex();

            var updated = false;
            if (pool == null)
            {
                pool = new AVMTradeReporter.Model.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = AVMTradeReporter.Model.Data.DEXProtocol.Tiny,
                    A = A.Value.Uint,
                    B = B.Value.Uint,
                    AssetIdLP = LTID.Value.Uint,
                    AMMType = Model.Data.AMMType.OldAMM,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = FEE_BPS.Value.Uint / 10000m,
                    ProtocolFeePortion = Convert.ToDecimal(TINY_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint),
                    AssetIdA = assetAId,
                    AssetIdB = assetBId
                };
                updated = true;
            }
            else
            {
                if (pool.Protocol != Model.Data.DEXProtocol.Tiny)
                {
                    pool.Protocol = Model.Data.DEXProtocol.Tiny;
                    updated = true;
                }
                if (pool.A != A.Value.Uint)
                {
                    pool.A = A.Value.Uint;
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
                if (pool.AMMType != Model.Data.AMMType.OldAMM)
                {
                    pool.AMMType = Model.Data.AMMType.OldAMM;
                    updated = true;
                }

                if (updated)
                {
                    pool.Timestamp = DateTimeOffset.Now;
                }


            }
            if (updated)
            {
                await _poolRepository.StorePoolAsync(pool, cancelationTokenSource.Token);
            }
            return pool;
        }
    }
}
