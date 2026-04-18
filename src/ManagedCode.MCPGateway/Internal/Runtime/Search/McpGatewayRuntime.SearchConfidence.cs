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
        string confidenceQuery,
        double rawScore
    )
    {
        var clampedRawScore = Math.Clamp(rawScore, 0d, 1d);
        if (clampedRawScore <= double.Epsilon)
        {
            return 0d;
        }

        var evidence = CalculateDescriptorQueryEvidence(confidenceQuery, entry);
        return Math.Clamp(
            (clampedRawScore + (GraphConfidenceEvidenceWeight * evidence))
                / (1d + GraphConfidenceEvidenceWeight),
            0d,
            1d
        );
    }

    private static double CalculateDescriptorQueryEvidence(
        string confidenceQuery,
        ToolCatalogEntry entry
    )
    {
        var queryTerms = BuildOrderedGraphTerms(confidenceQuery)
            .Take(GraphConfidenceMaxQueryTerms)
            .ToArray();
        if (queryTerms.Length == 0)
        {
            return 1d;
        }

        var descriptorTerms = BuildOrderedGraphTerms(entry.Document).ToArray();
        if (descriptorTerms.Length == 0)
        {
            return 0d;
        }

        var supportedWeight = 0d;
        var totalWeight = 0d;
        foreach (var queryTerm in queryTerms)
        {
            var weight = Math.Min(GraphConfidenceTermWeightCap, queryTerm.Length);
            totalWeight += weight;
            supportedWeight += weight * CalculateBestTermSimilarity(queryTerm, descriptorTerms);
        }

        return totalWeight <= double.Epsilon ? 1d : supportedWeight / totalWeight;
    }

    private static double CalculateBestTermSimilarity(
        string queryTerm,
        IReadOnlyList<string> descriptorTerms
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

    private static double CalculateTermSimilarity(string queryTerm, string descriptorTerm)
    {
        if (string.Equals(queryTerm, descriptorTerm, StringComparison.OrdinalIgnoreCase))
        {
            return 1d;
        }

        if (
            queryTerm.Length < GraphMinimumFuzzyTermLength
            || descriptorTerm.Length < GraphMinimumFuzzyTermLength
        )
        {
            return 0d;
        }

        if (
            descriptorTerm.Contains(queryTerm, StringComparison.OrdinalIgnoreCase)
            || queryTerm.Contains(descriptorTerm, StringComparison.OrdinalIgnoreCase)
        )
        {
            return GraphContainsTermSimilarity;
        }

        var similarity = CalculateDiceCoefficient(queryTerm, descriptorTerm);
        return similarity >= GraphMinimumFuzzySimilarity ? similarity : 0d;
    }

    private static double CalculateDiceCoefficient(string left, string right)
    {
        var leftBigrams = CreateCharacterBigrams(left);
        var rightBigrams = CreateCharacterBigrams(right);
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
