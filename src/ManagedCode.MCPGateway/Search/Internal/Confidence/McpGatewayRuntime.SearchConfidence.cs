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
        var clampedRawScore = Math.Clamp(rawScore, 0d, 1d);
        if (clampedRawScore <= double.Epsilon)
        {
            return 0d;
        }

        var evidence = CalculateDescriptorQueryEvidence(scoreContext, entry);
        return Math.Clamp(
            (clampedRawScore + (GraphConfidenceEvidenceWeight * evidence))
                / (1d + GraphConfidenceEvidenceWeight),
            0d,
            1d
        );
    }

    private static double CalculateDescriptorQueryEvidence(
        SearchScoreContext scoreContext,
        ToolCatalogEntry entry
    )
    {
        if (!scoreContext.HasConfidenceTerms)
        {
            return 1d;
        }

        if (entry.ConfidenceTerms.Count == 0)
        {
            return 0d;
        }

        var supportedWeight = 0d;
        var totalWeight = 0d;
        foreach (var queryTerm in scoreContext.ConfidenceTerms)
        {
            totalWeight += queryTerm.Weight;
            supportedWeight +=
                queryTerm.Weight * CalculateBestTermSimilarity(queryTerm, entry.ConfidenceTerms);
        }

        return totalWeight <= double.Epsilon ? 1d : supportedWeight / totalWeight;
    }

    private static double CalculateBestTermSimilarity(
        GraphConfidenceQueryTerm queryTerm,
        IReadOnlyList<GraphConfidenceDescriptorTerm> descriptorTerms
    )
    {
        var best = 0d;
        foreach (var descriptorTerm in descriptorTerms)
        {
            var similarity = CalculateTermSimilarity(queryTerm, descriptorTerm);
            if (similarity > best)
            {
                best = similarity;
            }

            if (best >= 1d)
            {
                return 1d;
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
            return 1d;
        }

        if (
            queryTerm.Value.Length < GraphMinimumFuzzyTermLength
            || descriptorTerm.Value.Length < GraphMinimumFuzzyTermLength
        )
        {
            return 0d;
        }

        if (
            descriptorTerm.Value.Contains(queryTerm.Value, StringComparison.OrdinalIgnoreCase)
            || queryTerm.Value.Contains(descriptorTerm.Value, StringComparison.OrdinalIgnoreCase)
        )
        {
            return GraphContainsTermSimilarity;
        }

        var similarity = CalculateDiceCoefficient(queryTerm.Bigrams, descriptorTerm.Bigrams);
        return similarity >= GraphMinimumFuzzySimilarity ? similarity : 0d;
    }

    private static double CalculateDiceCoefficient(
        IReadOnlySet<string> leftBigrams,
        IReadOnlySet<string> rightBigrams
    )
    {
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0d;
        }

        var overlap = 0;
        foreach (var bigram in leftBigrams)
        {
            if (rightBigrams.Contains(bigram))
            {
                overlap++;
            }
        }

        return (2d * overlap) / (leftBigrams.Count + rightBigrams.Count);
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
