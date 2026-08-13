using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Route selection for an asset's cached USD price (AggregatedPoolRepository.SelectAssetUsdPrice).
    ///
    /// Background (production incident, 2026-08-12): UpdateRelatedAssetsAsync unconditionally
    /// preferred the direct asset↔UsdReferenceAsset pair, so a Pact pool holding 155 MEEP and
    /// $0.000147 of USDC priced MEEP at $9.4e-7 — 214x above the two MEEP/ALGO pools holding
    /// ~$3,000 that agreed with actual trades at ~$4.5e-9. The Top gainers card then showed
    /// systematic fake ~+99% "gains" (current price = full wrong cache, 24h baseline = polluted
    /// candle at roughly half of it).
    ///
    /// Contract: the price comes from the DEEPER market, measured by the real USD value of the
    /// anchor-side liquidity (USDC side of the direct pair; ALGO side × ALGO price for the ALGO
    /// route), not from a fixed route preference.
    /// </summary>
    public class AssetUsdPriceRouteSelectionTests
    {
        private const ulong Usdc = 31566704UL;
        private const ulong Meep = 2582590415UL;

        private static AggregatedPool Pair(ulong a, ulong b, decimal virtA, decimal virtB, decimal tvlA, decimal tvlB) => new()
        {
            AssetIdA = a,
            AssetIdB = b,
            VirtualSumALevel1ForPrice = virtA,
            VirtualSumBLevel1ForPrice = virtB,
            TVL_A = tvlA,
            TVL_B = tvlB,
            // Route selection reads *_ForPrice as depth; keep it equal to TVL_A/TVL_B here so pairs built by
            // this helper behave as fully price-voting (no wall/depleted pools involved) unless a test says otherwise.
            TVL_A_ForPrice = tvlA,
            TVL_B_ForPrice = tvlB
        };

        [Test]
        public void DustDirectUsdPool_LosesToLiquidAlgoRoute()
        {
            // The live MEEP shape: direct USDC pool with $0.000147, ALGO pair with ~18k ALGO.
            var direct = Pair(Usdc, Meep, virtA: 0.000147m, virtB: 155.815322m, tvlA: 0.000147m, tvlB: 155.815322m);
            // (0, MEEP): 18k ALGO vs 347G MEEP → 5.176e-8 ALGO per MEEP.
            var viaAlgo = Pair(0UL, Meep, virtA: 17986m, virtB: 347_493_218_626m, tvlA: 17986m, tvlB: 347_493_218_626m);

            var price = AggregatedPoolRepository.SelectAssetUsdPrice(Meep, Usdc, direct, viaAlgo, algoPriceUsd: 0.088m);

            Assert.That(price, Is.Not.Null);
            // 17986/347493218626 × 0.088 ≈ 4.555e-9 — NOT the dust pool's 9.43e-7.
            Assert.That(price!.Value, Is.EqualTo(17986m / 347_493_218_626m * 0.088m).Within(0.000000000001m));
        }

        [Test]
        public void LiquidDirectUsdPool_WinsOverThinnerAlgoRoute()
        {
            // Healthy asset: $500k USDC pool vs an ALGO pool holding ~$10k worth of ALGO.
            var direct = Pair(5UL, Usdc, virtA: 250_000m, virtB: 500_000m, tvlA: 250_000m, tvlB: 500_000m);
            var viaAlgo = Pair(0UL, 5UL, virtA: 100_000m, virtB: 55_000m, tvlA: 100_000m, tvlB: 55_000m);

            var price = AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, direct, viaAlgo, algoPriceUsd: 0.09m);

            Assert.That(price, Is.EqualTo(2m), "direct pair price = 500k USDC / 250k X = $2");
        }

        [Test]
        public void OnlyDirectPairExists_IsUsed()
        {
            var direct = Pair(5UL, Usdc, 1000m, 2000m, 1000m, 2000m);
            var price = AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, direct, null, algoPriceUsd: 0.09m);
            Assert.That(price, Is.EqualTo(2m));
        }

        [Test]
        public void OnlyAlgoRouteExists_IsUsed()
        {
            var viaAlgo = Pair(0UL, 5UL, virtA: 900m, virtB: 100m, tvlA: 900m, tvlB: 100m);
            var price = AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, null, viaAlgo, algoPriceUsd: 0.10m);
            Assert.That(price, Is.EqualTo(900m / 100m * 0.10m), "9 ALGO per X × $0.10 = $0.90");
        }

        [Test]
        public void AlgoRouteWithoutAlgoPrice_CannotPrice()
        {
            var viaAlgo = Pair(0UL, 5UL, 900m, 100m, 900m, 100m);
            Assert.That(AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, null, viaAlgo, algoPriceUsd: null), Is.Null);
            Assert.That(AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, null, viaAlgo, algoPriceUsd: 0m), Is.Null);
        }

        [Test]
        public void ReversedOrientation_IsNormalized()
        {
            // Same pools as the MEEP case but handed over in the opposite stored orientation.
            var direct = Pair(Usdc, Meep, 0.000147m, 155.815322m, 0.000147m, 155.815322m).Reverse();
            var viaAlgo = Pair(0UL, Meep, 17986m, 347_493_218_626m, 17986m, 347_493_218_626m).Reverse();

            var price = AggregatedPoolRepository.SelectAssetUsdPrice(Meep, Usdc, direct, viaAlgo, algoPriceUsd: 0.088m);

            Assert.That(price, Is.Not.Null);
            Assert.That(price!.Value, Is.EqualTo(17986m / 347_493_218_626m * 0.088m).Within(0.000000000001m));
        }

        [Test]
        public void PairsWithoutPriceableVirtualSums_AreIgnored()
        {
            // Zero virtual sums (e.g. only empty pools) cannot produce a price on either route.
            var direct = Pair(5UL, Usdc, 0m, 0m, 0m, 0m);
            var viaAlgo = Pair(0UL, 5UL, 0m, 0m, 0m, 0m);
            Assert.That(AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, direct, viaAlgo, 0.09m), Is.Null);
        }

        [Test]
        public void NoPairsAtAll_ReturnsNull()
        {
            Assert.That(AggregatedPoolRepository.SelectAssetUsdPrice(5UL, Usdc, null, null, 0.09m), Is.Null);
        }

        [Test]
        public void DepletedOutOfRangeClammRealBalance_DoesNotInflateAlgoRouteDepth()
        {
            // Production incident, 2026-08-13: GoldDAO's aggregated (0, GoldDAO) pair included two
            // out-of-range Biatec CLAMM pools holding ~63k real ALGO but effectively zero real GoldDAO
            // (all liquidity converted to one side after price moved outside their [PMin,PMax] range).
            // TVL_B on the raw group counted that ~63k ALGO as "depth" even though AggregatedPool.FromPools
            // had already excluded those same pools from VirtualSumBLevel1ForPrice/VirtualSumALevel1ForPrice
            // via Pool.IsDepletedSingleSided - so the route's *price* was clean but its *depth* still won
            // the comparison against the accurate, genuinely deep-enough USDC pool, and the wrong price
            // ($0.317 instead of the ~$0.90 every real two-sided pool agreed on) got selected anyway.
            ulong goldDao = 1241945177UL;

            // TVL_A/TVL_B (unfiltered) include the ~63k ALGO stuck in the two depleted CLAMM pools.
            // TVL_A_ForPrice/TVL_B_ForPrice (price-voting pools only: the two real Pact pools) do not.
            var viaAlgo = new AggregatedPool
            {
                AssetIdA = 0UL,
                AssetIdB = goldDao,
                VirtualSumALevel1ForPrice = 135_336.212m, // matches VirtualSumALevel1ForPrice from the clean price-voting pools
                VirtualSumBLevel1ForPrice = 33_702.751m,
                TVL_A = 96_025.454981m, // includes the two depleted CLAMM pools' real ALGO
                TVL_B = 2_879.569628m,
                TVL_A_ForPrice = 32_592.273720m, // only the two real Pact pools
                TVL_B_ForPrice = 2_879.569628m,
            };
            var direct = new AggregatedPool
            {
                AssetIdA = goldDao,
                AssetIdB = Usdc,
                VirtualSumALevel1ForPrice = 3_093.129159m,
                VirtualSumBLevel1ForPrice = 2_776.578908m,
                TVL_A = 3_093.129159m,
                TVL_B = 2_776.578908m,
                TVL_A_ForPrice = 3_093.129159m,
                TVL_B_ForPrice = 2_776.578908m,
            };

            var price = AggregatedPoolRepository.SelectAssetUsdPrice(goldDao, Usdc, direct, viaAlgo, algoPriceUsd: 0.079055m);

            // The accurate direct USDC price (~$0.897), not the ALGO route corrupted by depleted-pool depth (~$0.317).
            Assert.That(price, Is.EqualTo(2_776.578908m / 3_093.129159m).Within(0.0000001m));
        }
    }
}
