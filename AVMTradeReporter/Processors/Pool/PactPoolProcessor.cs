using Algorand.Algod;
using AVMTradeReporter.Extensions;
using AVMTradeReporter.Repository;
using System.Text;

namespace AVMTradeReporter.Processors.Pool
{
    public class PactPoolProcessor : IPoolProcessor
    {
        private IDefaultApi _algod;
        private IPoolRepository _poolRepository;
        ILogger<PactPoolProcessor> _logger;
        public PactPoolProcessor(
            IDefaultApi algod, 
            IPoolRepository poolRepository, 
            ILogger<PactPoolProcessor> logger)
        {
            _algod = algod;
            _poolRepository = poolRepository;
            _logger = logger;
        }

        public async Task<AVMTradeReporter.Model.Data.Pool> LoadPoolAsync(string address, ulong appId)
        {
            using var cancelationTokenSource = new CancellationTokenSource();
            var pool = await _poolRepository.GetPoolAsync(address, cancelationTokenSource.Token);
            var app  = await _algod.GetApplicationByIDAsync(appId);

            var A = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("A")));
            if (A == null) throw new Exception("A is missing in global params");
            var B = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("B")));
            if (B == null) throw new Exception("B is missing in global params");
            var L = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("L")));
            if (L == null) throw new Exception("L is missing in global params");
            var LTID = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("LTID")));
            if (LTID == null) throw new Exception("LTID is missing in global params");
            var FEE_BPS = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("FEE_BPS")));
            if (FEE_BPS == null) throw new Exception("FEE_BPS is missing in global params");
            var PACT_FEE_BPS = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("PACT_FEE_BPS")));
            if (PACT_FEE_BPS == null) throw new Exception("PACT_FEE_BPS is missing in global params");
            var CONFIG = app.Params.GlobalState.FirstOrDefault(p => p.Key == Convert.ToBase64String(Encoding.ASCII.GetBytes("CONFIG")));
            if (CONFIG == null) throw new Exception("CONFIG is missing in global params");
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

            var updated = false;
            if(pool == null)
            {
                pool = new AVMTradeReporter.Model.Data.Pool
                {
                    PoolAddress = address,
                    PoolAppId = appId,
                    Protocol = AVMTradeReporter.Model.Data.DEXProtocol.Pact,
                    A = A.Value.Uint,
                    B = B.Value.Uint,
                    L = L.Value.Uint,
                    AssetIdLP = LTID.Value.Uint,
                    AMMType = Model.Data.AMMType.OldAMM,
                    Timestamp = DateTimeOffset.Now,
                    ApprovalProgramHash = hash,
                    LPFee = FEE_BPS.Value.Uint / 10000m,
                    ProtocolFeePortion = Convert.ToDecimal(PACT_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint),
                    AssetIdA = assetAId,
                    AssetIdB = assetBId
                };
                updated = true;
            }
            else
            {
                if(pool.Protocol != Model.Data.DEXProtocol.Pact) { 
                    pool.Protocol = Model.Data.DEXProtocol.Pact;
                    updated = true;
                }
                if(pool.A != A.Value.Uint)
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
                if (pool.LPFee != FEE_BPS.Value.Uint / 10000m)
                {
                    pool.LPFee = FEE_BPS.Value.Uint / 10000m;
                    updated = true;
                }
                if (pool.ProtocolFeePortion != Convert.ToDecimal(PACT_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint))
                {
                    pool.ProtocolFeePortion = Convert.ToDecimal(PACT_FEE_BPS.Value.Uint) / Convert.ToDecimal(FEE_BPS.Value.Uint);
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
                _logger.LogInformation("Pool {appId} {appAddress} updated with pool refresh", pool.PoolAppId, pool.PoolAddress);
                await _poolRepository.StorePoolAsync(pool, cancelationTokenSource.Token);
            }
            return pool;
        }
    }
}
