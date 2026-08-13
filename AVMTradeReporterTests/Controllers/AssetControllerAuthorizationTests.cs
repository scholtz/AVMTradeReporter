using System.Reflection;
using AVMTradeReporter.Controllers;
using Microsoft.AspNetCore.Authorization;
using NUnit.Framework;

namespace AVMTradeReporterTests.Controllers
{
    /// <summary>
    /// Regression coverage for https://github.com/scholtz/AVMTradeReporter - the 2026-08-12 "secure
    /// remaining public endpoints" commit added a controller-level [Authorize] to AssetController,
    /// which silently broke the TradingView charting widget: its datafeed (biatec-charting-widget,
    /// a separate unauthenticated browser client with no ARC-14 signing capability) resolves an
    /// asset id to a ticker by calling GET /api/asset anonymously. Once that endpoint started
    /// requiring auth, every chart on mainnet fell back to its hardcoded default symbol
    /// ("ALGORANDUSD") with all-zero OHLC values instead of the asset actually being viewed.
    /// GetAssets must stay reachable without authentication - same public/read-only trust level as
    /// the asset image endpoint and the OHLC controller, which were deliberately kept public in
    /// that same commit for exactly this reason.
    /// </summary>
    public class AssetControllerAuthorizationTests
    {
        [Test]
        public void GetAssets_AllowsAnonymousAccess()
        {
            var method = typeof(AssetController).GetMethod(nameof(AssetController.GetAssets));
            Assert.That(method, Is.Not.Null, "GetAssets action not found via reflection");

            var actionHasAllowAnonymous = method!.GetCustomAttribute<AllowAnonymousAttribute>() != null;
            var controllerHasAllowAnonymous = typeof(AssetController).GetCustomAttribute<AllowAnonymousAttribute>() != null;

            Assert.That(actionHasAllowAnonymous || controllerHasAllowAnonymous, Is.True,
                "GET /api/asset must stay reachable without authentication - the TradingView " +
                "charting widget (biatec-charting-widget) resolves asset id -> ticker via this " +
                "endpoint with a plain unauthenticated fetch, and has no ARC-14 signing capability.");
        }

        [Test]
        public void GetAssetImage_AllowsAnonymousAccess()
        {
            // Existing, already-correct behavior - guards against a future regression re-adding
            // auth here the same way it was accidentally added to GetAssets.
            var method = typeof(AssetController).GetMethod(nameof(AssetController.GetAssetImage));
            Assert.That(method, Is.Not.Null, "GetAssetImage action not found via reflection");

            var hasAllowAnonymous = method!.GetCustomAttribute<AllowAnonymousAttribute>() != null;
            Assert.That(hasAllowAnonymous, Is.True, "GET /api/asset/image/{assetId} must stay public.");
        }
    }
}
