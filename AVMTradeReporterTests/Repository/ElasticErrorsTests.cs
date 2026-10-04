using AVMTradeReporter.Repository;

namespace AVMTradeReporterTests.Repository
{
    public class ElasticErrorsTests
    {
        private static Exception NotFound(string index) =>
            new InvalidOperationException($"Request failed to execute. Call: Status code 404 from: POST /{index}/_search. ServerError: Type: index_not_found_exception Reason: \"no such index [{index}]\"");

        [Test]
        public void ARecognisedAnswer_IsFoundDirectly_AndInInnerExceptions()
        {
            Assert.That(ElasticErrors.IsIndexNotFound(NotFound("trades"), "trades"), Is.True);
            Assert.That(ElasticErrors.IsIndexNotFound(new Exception("outer", new Exception("middle", NotFound("trades"))), "trades"), Is.True);
        }

        [Test]
        public void ANotFoundForAnotherIndex_IsNotTheAnswerForThisOne()
        {
            Assert.That(ElasticErrors.IsIndexNotFound(NotFound("pools"), "trades"), Is.False);
        }

        [Test]
        public void OtherFailures_AreNotAnswers()
        {
            Assert.That(ElasticErrors.IsIndexNotFound(null, "trades"), Is.False);
            Assert.That(ElasticErrors.IsIndexNotFound(new HttpRequestException("Connection refused"), "trades"), Is.False);
            Assert.That(ElasticErrors.IsIndexNotFound(new Exception("Status code 404 from a proxy"), "trades"), Is.False);
        }

        [Test]
        public void TheInnerExceptionWalk_IsBounded()
        {
            Exception chain = NotFound("trades");
            for (var i = 0; i < 20; i++) chain = new Exception("wrapper " + i, chain);
            Assert.That(ElasticErrors.IsIndexNotFound(chain, "trades"), Is.False, "a pathologically deep chain is not followed for ever");
        }
    }
}
