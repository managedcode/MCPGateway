namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static void AddLowConfidenceGraphDiagnostic(
        RankedSearch rankedSearch,
        IList<McpGatewayDiagnostic> diagnostics
    )
    {
        if (!IsLowConfidenceGraphResult(rankedSearch, includeEmpty: false))
        {
            return;
        }

        diagnostics.Add(
            new McpGatewayDiagnostic(
                LowConfidenceResultsDiagnosticCode,
                LowConfidenceResultsMessage
            )
        );
    }

    private static double CalibrateGraphConfidence(
        ToolCatalogEntry entry,
        SearchScoreContext scoreContext,
        double rawScore
    )
    {
        var clampedRawScore = Math.Clamp(rawScore, SearchScoreMinimum, SearchScoreMaximum);
        if (clampedRawScore <= double.Epsilon)
        {
            return SearchScoreMinimum;
        }

        var evidence = CalculateDescriptorQueryEvidence(scoreContext, entry);
        return Math.Clamp(
            (
                (GraphConfidenceRawScoreWeight * clampedRawScore)
                + (GraphConfidenceEvidenceWeight * evidence)
            ) / (GraphConfidenceRawScoreWeight + GraphConfidenceEvidenceWeight),
            SearchScoreMinimum,
            SearchScoreMaximum
        );
    }

    private static double CalculateDescriptorQueryEvidence(
        SearchScoreContext scoreContext,
        ToolCatalogEntry entry
    )
    {
        if (!scoreContext.HasConfidenceTerms)
        {
            return SearchScoreMaximum;
        }

        if (entry.ConfidenceTerms.Count == 0)
        {
            return SearchScoreMinimum;
        }

        var supportedWeight = SearchScoreMinimum;
        var totalWeight = SearchScoreMinimum;
        foreach (var queryTerm in scoreContext.ConfidenceTerms)
        {
            totalWeight += queryTerm.Weight;
            supportedWeight +=
                queryTerm.Weight * CalculateBestTermSimilarity(queryTerm, entry.ConfidenceTerms);
        }

        return totalWeight <= double.Epsilon ? SearchScoreMaximum : supportedWeight / totalWeight;
    }

    private static double CalculateBestTermSimilarity(
        GraphConfidenceQueryTerm queryTerm,
        IReadOnlyList<GraphConfidenceDescriptorTerm> descriptorTerms
    )
    {
        var best = SearchScoreMinimum;
        foreach (var descriptorTerm in descriptorTerms)
        {
            var similarity = CalculateTermSimilarity(queryTerm, descriptorTerm);
            if (similarity > best)
            {
                best = similarity;
            }

            if (best >= SearchScoreMaximum)
            {
                return SearchScoreMaximum;
            }
        }

        return best;
    }

    private static double CalculateTermSimilarity(
        GraphConfidenceQueryTerm queryTerm,
        GraphConfidenceDescriptorTerm descriptorTerm
    )
    {
        if (string.Equals(queryTerm.Value, descriptorTerm.Value, StringComparison.OrdinalIgnoreCase))
        {
            return SearchScoreMaximum;
        }

        if (
            queryTerm.Value.Length < GraphMinimumFuzzyTermLength
            || descriptorTerm.Value.Length < GraphMinimumFuzzyTermLength
        )
        {
            return SearchScoreMinimum;
        }

        if (
            descriptorTerm.Value.Contains(queryTerm.Value, StringComparison.OrdinalIgnoreCase)
            || queryTerm.Value.Contains(descriptorTerm.Value, StringComparison.OrdinalIgnoreCase)
        )
        {
            return GraphContainsTermSimilarity;
        }

        var similarity = CalculateDiceCoefficient(queryTerm.Bigrams, descriptorTerm.Bigrams);
        return similarity >= GraphMinimumFuzzySimilarity ? similarity : SearchScoreMinimum;
    }

    private static double CalculateDiceCoefficient(
        IReadOnlySet<string> leftBigrams,
        IReadOnlySet<string> rightBigrams
    )
    {
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return SearchScoreMinimum;
        }

        var overlap = 0;
        foreach (var bigram in leftBigrams)
        {
            if (rightBigrams.Contains(bigram))
            {
                overlap++;
            }
        }

        return (GraphConfidenceDiceCoefficientScale * overlap)
            / (leftBigrams.Count + rightBigrams.Count);
    }

    private static HashSet<string> CreateCharacterBigrams(string term)
    {
        var bigrams = new HashSet<string>(StringComparer.Ordinal);
        if (term.Length < 2)
        {
            if (term.Length == 1)
            {
                bigrams.Add(term);
            }

            return bigrams;
        }

        for (var index = 0; index < term.Length - 1; index++)
        {
            bigrams.Add(term.Substring(index, 2));
        }

        return bigrams;
    }

    private static bool IsLowConfidenceGraphResult(
        RankedSearch rankedSearch,
        bool includeEmpty = true
    )
    {
        if (!string.Equals(rankedSearch.RankingMode, SearchModeGraph, StringComparison.Ordinal))
        {
            return false;
        }

        if (rankedSearch.Ranked.Count == 0)
        {
            return includeEmpty;
        }

        return rankedSearch.Ranked[0].Score < MinimumGraphMatchConfidence;
    }
}
