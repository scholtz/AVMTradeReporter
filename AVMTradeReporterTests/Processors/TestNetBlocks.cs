using Algorand.Algod;
using Algorand.Algod.Model;

namespace AVMTradeReporterTests.Processors
{
    /// <summary>
    /// Helper for fetching Algorand testnet blocks in tests (Pact CLAMM is currently deployed on testnet only).
    /// </summary>
    public static class TestNetBlocks
    {
        public const string TestNetAlgodUrl = "https://testnet-api.4160.nodely.dev";

        public static DefaultApi CreateAlgod()
        {
            var httpClient = new HttpClient { BaseAddress = new Uri(TestNetAlgodUrl) };
            return new DefaultApi(httpClient);
        }

        public static async Task<CertifiedBlock> FetchBlockAsync(ulong round)
        {
            var algod = CreateAlgod();
            return await algod.GetBlockAsync(round, Format.Msgpack, false);
        }
    }
}
