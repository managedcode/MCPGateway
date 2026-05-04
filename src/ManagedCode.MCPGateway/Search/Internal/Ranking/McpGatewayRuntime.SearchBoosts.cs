namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static ToolSearchTermIndex BuildToolSearchTermIndex(string document)
    {
        var searchBoostTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confidenceTerms = new List<GraphConfidenceDescriptorTerm>();

        foreach (var term in BuildOrderedGraphTerms(document))
        {
            if (searchBoostTerms.Add(term))
            {
                confidenceTerms.Add(new GraphConfidenceDescriptorTerm(term, CreateCharacterBigrams(term)));
            }
        }

        return new ToolSearchTermIndex(searchBoostTerms, confidenceTerms);
    }

    private static SearchScoreContext CreateSearchScoreContext(string boostQuery)
    {
        if (string.IsNullOrWhiteSpace(boostQuery))
        {
            return SearchScoreContext.Empty;
        }

        var boostTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confidenceTerms = new List<GraphConfidenceQueryTerm>();

        foreach (var term in BuildOrderedGraphTerms(boostQuery))
        {
            if (!boostTerms.Add(term))
            {
                continue;
            }

            if (confidenceTerms.Count < GraphConfidenceMaxQueryTerms)
            {
                confidenceTerms.Add(
                    new GraphConfidenceQueryTerm(
                        term,
                        Math.Min(GraphConfidenceTermWeightCap, term.Length),
                        CreateCharacterBigrams(term)
                    )
                );
            }
        }

        return boostTerms.Count == 0
            ? SearchScoreContext.Empty
            : new SearchScoreContext(boostTerms, confidenceTerms);
    }

    private static double ApplySearchBoosts(
        ToolCatalogEntry entry,
        SearchScoreContext scoreContext,
        double score
    )
    {
        if (!scoreContext.HasBoostTerms || entry.SearchBoostTerms.Count == 0)
        {
            return score;
        }

        var overlap = 0;
        foreach (var queryTerm in scoreContext.BoostTerms)
        {
            if (entry.SearchBoostTerms.Contains(queryTerm))
            {
                overlap++;
            }
        }

        if (overlap == 0)
        {
            return score;
        }

        var coverage = (double)overlap / scoreContext.BoostTerms.Count;
        return Math.Min(SearchScoreMaximum, score + (coverage * ToolNameSignalWeight));
    }
}
