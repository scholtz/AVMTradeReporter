using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Exact-match filters that work with both mappings of the string fields in the wild: production's
    /// "trades" index predates the index template and was dynamically mapped (text with a ".keyword" subfield), fresh
    /// clusters (stage/testnet) use the template (pure keyword fields, NO ".keyword" subfield). Querying only
    /// "field.keyword" silently matches nothing on template-mapped clusters, so both variants are ORed - a term query
    /// on an unmapped field is not an error, it just matches nothing.
    /// </summary>
    internal static class ElasticKeywordQuery
    {
        public static void DualKeywordTerm<T>(QueryDescriptor<T> query, string field, string value)
        {
            query.Bool(b => b
                .Should(
                    s => s.Term(t => t.Field(field).Value(FieldValue.String(value))),
                    s => s.Term(t => t.Field(field + ".keyword").Value(FieldValue.String(value))))
                .MinimumShouldMatch(1));
        }

        public static void DualKeywordTerms<T>(QueryDescriptor<T> query, string field, IEnumerable<string> values)
        {
            var terms = values.Select(FieldValue.String).ToArray();
            query.Bool(b => b
                .Should(
                    s => s.Terms(t => t.Field(field).Terms(new TermsQueryField(terms))),
                    s => s.Terms(t => t.Field(field + ".keyword").Terms(new TermsQueryField(terms))))
                .MinimumShouldMatch(1));
        }
    }
}
