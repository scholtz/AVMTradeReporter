using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data.Enums;
using Microsoft.Extensions.Configuration;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class CoinGeckoConfigurationTests
    {
        private static CoinGeckoConfiguration Bind(string json)
        {
            var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
            return config.GetSection("AppConfiguration").Get<AppConfiguration>()!.CoinGecko;
        }

        [Test]
        public void Default_PublishesOnlyTheBiatecDex()
        {
            Assert.That(new CoinGeckoConfiguration().PublishedProtocols, Is.EqualTo(new[] { DEXProtocol.Biatec }));
            Assert.That(Bind("{\"AppConfiguration\":{\"IndexerId\":\"x\"}}").PublishedProtocols, Is.EqualTo(new[] { DEXProtocol.Biatec }));
        }

        [Test]
        public void ExplicitBiatec_IsNotDuplicatedByConfigurationBinding()
        {
            // binding appends to a pre-filled list: the documented appsettings value must not yield [Biatec, Biatec]
            var bound = Bind("{\"AppConfiguration\":{\"CoinGecko\":{\"Protocols\":[\"Biatec\"]}}}");
            Assert.That(bound.PublishedProtocols, Is.EqualTo(new[] { DEXProtocol.Biatec }));
        }

        [Test]
        public void ConfiguredProtocols_ReplaceTheDefault()
        {
            var bound = Bind("{\"AppConfiguration\":{\"CoinGecko\":{\"Protocols\":[\"Pact\"]}}}");
            Assert.That(bound.PublishedProtocols, Is.EqualTo(new[] { DEXProtocol.Pact }), "Biatec must be removable");
        }

        [Test]
        public void RateLimit_IsLargerThanTheAnonymousBudgetOfTheRestOfTheApi()
        {
            // the indexer polls latest-block + events about every 2 s = 60 requests/min before asset/pair lookups
            Assert.That(new CoinGeckoConfiguration().RateLimitPerMinute, Is.GreaterThan(120));
        }
    }
}
