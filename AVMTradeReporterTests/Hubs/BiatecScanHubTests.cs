using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Subscription;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using NUnit.Framework;

namespace AVMTradeReporterTests.Hubs
{
    public class BiatecScanHubTests
    {
        [Test]
        public void ShouldSendTradeToUser_WithTraderSubscription_ReturnsTrue()
        {
            var trade = new Trade { Trader = "TRADER" };
            var filter = new SubscriptionFilter { Traders = ["TRADER"] };

            Assert.That(BiatecScanHub.ShouldSendTradeToUser(trade, filter), Is.True);
        }

        [Test]
        public void ShouldSendTradeToUser_WithProtocolSubscription_ReturnsTrue()
        {
            var trade = new Trade { Protocol = DEXProtocol.Tiny };
            var filter = new SubscriptionFilter { Protocols = ["Tiny"] };

            Assert.That(BiatecScanHub.ShouldSendTradeToUser(trade, filter), Is.True);
        }

        [Test]
        public void ShouldSendTradeToUser_WithTradeStateSubscription_ReturnsTrue()
        {
            var trade = new Trade { TradeState = TxState.Confirmed };
            var filter = new SubscriptionFilter { TradeStates = ["Confirmed"] };

            Assert.That(BiatecScanHub.ShouldSendTradeToUser(trade, filter), Is.True);
        }

        [Test]
        public void ShouldSendTradeToUser_WithMinimumValueSubscription_ReturnsTrueOnlyWhenValueMatches()
        {
            var matchingTrade = new Trade { ValueUSD = 100m };
            var smallTrade = new Trade { ValueUSD = 99m };
            var filter = new SubscriptionFilter { MinTradeValueUSD = 100m };

            Assert.That(BiatecScanHub.ShouldSendTradeToUser(matchingTrade, filter), Is.True);
            Assert.That(BiatecScanHub.ShouldSendTradeToUser(smallTrade, filter), Is.False);
        }

        [Test]
        public void ShouldSendTradeToUser_WithUnorderedPairSubscription_ReturnsTrueForBothDirections()
        {
            var filter = new SubscriptionFilter { AggregatedPoolsIds = ["1-2"] };

            Assert.That(BiatecScanHub.ShouldSendTradeToUser(new Trade { AssetIdIn = 1, AssetIdOut = 2 }, filter), Is.True);
            Assert.That(BiatecScanHub.ShouldSendTradeToUser(new Trade { AssetIdIn = 2, AssetIdOut = 1 }, filter), Is.True);
        }
    }
}
