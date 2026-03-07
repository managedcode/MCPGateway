namespace ManagedCode.MCPGateway;

public sealed partial class McpGateway
{
    private static double ApplySearchBoosts(ToolCatalogEntry entry, string query, double score)
    {
        if (string.Equals(entry.Descriptor.ToolName, query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.1d;
        }
        else if (entry.Descriptor.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.03d;
        }

        return Math.Clamp(score, 0d, 1d);
    }

    private IReadOnlyList<ScoredToolEntry> RankLexically(
        ToolCatalogSnapshot snapshot,
        McpGatewaySearchRequest request,
        string effectiveQuery)
    {
        var queryProfile = BuildTokenSearchProfile(
            BuildQueryTokenSearchSegments(request),
            snapshot.TokenInverseDocumentFrequencies);
        if (queryProfile.TermWeights.Count == 0)
        {
            return RankLegacyLexically(snapshot.Entries, effectiveQuery);
        }

        var lexicalSearchTerms = BuildLexicalTerms(BuildQueryTokenSearchSegments(request));
        var rawQuery = request.Query?.Trim() ?? effectiveQuery;
        return snapshot.Entries
            .Select(entry => new ScoredToolEntry(entry, CalculateTokenSearchScore(
                entry,
                queryProfile,
                lexicalSearchTerms,
                rawQuery)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ScoredToolEntry> RankLegacyLexically(
        IReadOnlyList<ToolCatalogEntry> entries,
        string query)
    {
        var searchTerms = BuildSearchTerms(query);
        return entries
            .Select(entry => new ScoredToolEntry(entry, CalculateLegacyLexicalScore(entry, query, searchTerms)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double CalculateTokenSearchScore(
        ToolCatalogEntry entry,
        TokenSearchProfile queryProfile,
        IReadOnlySet<string> lexicalSearchTerms,
        string rawQuery)
    {
        var tokenSimilarity = CalculateSparseCosine(entry.TokenProfile, queryProfile);
        var tokenCoverage = CalculateQueryCoverage(entry.TokenProfile, queryProfile);
        var lexicalSimilarity = CalculateLexicalSimilarity(entry, rawQuery, lexicalSearchTerms);
        var approximateTermSimilarity = CalculateApproximateTermSimilarity(entry, lexicalSearchTerms);
        var score =
            (tokenSimilarity * TokenSimilarityWeight) +
            (tokenCoverage * TokenCoverageWeight) +
            (lexicalSimilarity * LexicalSimilarityWeight) +
            (approximateTermSimilarity * ApproximateTermSimilarityWeight);
        return ApplySearchBoosts(entry, rawQuery, score);
    }

    private static double CalculateSparseCosine(
        TokenSearchProfile entryProfile,
        TokenSearchProfile queryProfile)
    {
        if (entryProfile.Magnitude <= double.Epsilon ||
            queryProfile.Magnitude <= double.Epsilon)
        {
            return 0d;
        }

        var smaller = entryProfile.TermWeights.Count <= queryProfile.TermWeights.Count
            ? entryProfile.TermWeights
            : queryProfile.TermWeights;
        var larger = ReferenceEquals(smaller, entryProfile.TermWeights)
            ? queryProfile.TermWeights
            : entryProfile.TermWeights;

        var dot = 0d;
        foreach (var (term, weight) in smaller)
        {
            if (larger.TryGetValue(term, out var otherWeight))
            {
                dot += weight * otherWeight;
            }
        }

        return dot <= double.Epsilon
            ? 0d
            : dot / (entryProfile.Magnitude * queryProfile.Magnitude);
    }

    private static double CalculateQueryCoverage(
        TokenSearchProfile entryProfile,
        TokenSearchProfile queryProfile)
    {
        if (queryProfile.TotalWeight <= double.Epsilon)
        {
            return 0d;
        }

        var matchedWeight = 0d;
        foreach (var (term, weight) in queryProfile.TermWeights)
        {
            if (entryProfile.TermWeights.ContainsKey(term))
            {
                matchedWeight += weight;
            }
        }

        return matchedWeight <= double.Epsilon
            ? 0d
            : Math.Clamp(matchedWeight / queryProfile.TotalWeight, 0d, 1d);
    }

    private static double CalculateLexicalSimilarity(
        ToolCatalogEntry entry,
        string query,
        IReadOnlySet<string> searchTerms)
    {
        if (searchTerms.Count == 0)
        {
            return 0d;
        }

        var corpus = entry.LexicalTerms;

        var score = 0d;
        foreach (var term in searchTerms)
        {
            if (corpus.Contains(term))
            {
                score += 1d;
                continue;
            }

            if (corpus.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.35d;
            }
        }

        if (entry.Descriptor.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 2d;
        }

        return Math.Clamp(score / (searchTerms.Count + 2d), 0d, 1d);
    }

    private static double CalculateLegacyLexicalScore(
        ToolCatalogEntry entry,
        string query,
        IReadOnlySet<string> searchTerms)
    {
        var lexicalSimilarity = CalculateLexicalSimilarity(entry, query, searchTerms);
        var approximateTermSimilarity = CalculateApproximateTermSimilarity(entry, searchTerms);
        var score =
            (lexicalSimilarity * 0.75d) +
            (approximateTermSimilarity * 0.25d);
        return ApplySearchBoosts(entry, query, score);
    }

    private static double CalculateApproximateTermSimilarity(
        ToolCatalogEntry entry,
        IReadOnlySet<string> searchTerms)
    {
        if (searchTerms.Count == 0)
        {
            return 0d;
        }

        var fuzzyScore = 0d;
        var fuzzyTerms = 0;
        foreach (var term in searchTerms)
        {
            if (entry.LexicalTerms.Contains(term) ||
                entry.LexicalTerms.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var bestSimilarity = 0d;
            foreach (var candidate in entry.LexicalTerms)
            {
                var similarity = CalculateApproximateTermSimilarity(term, candidate);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                }
            }

            if (bestSimilarity > double.Epsilon)
            {
                fuzzyScore += bestSimilarity;
                fuzzyTerms++;
            }
        }

        return fuzzyTerms == 0
            ? 0d
            : Math.Clamp(fuzzyScore / fuzzyTerms, 0d, 1d);
    }

    private static double CalculateApproximateTermSimilarity(string source, string candidate)
    {
        if (source.Length < 4 ||
            candidate.Length < 4 ||
            Math.Abs(source.Length - candidate.Length) > 2)
        {
            return 0d;
        }

        var distanceThreshold = source.Length >= 8 || candidate.Length >= 8
            ? 2
            : 1;
        var distance = CalculateDamerauLevenshteinDistance(source, candidate, distanceThreshold);
        if (distance > distanceThreshold)
        {
            return 0d;
        }

        return 1d - (distance / (double)Math.Max(source.Length, candidate.Length));
    }

    private static int CalculateDamerauLevenshteinDistance(
        string source,
        string candidate,
        int maxDistance)
    {
        if (string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (source.Length == 0)
        {
            return candidate.Length;
        }

        if (candidate.Length == 0)
        {
            return source.Length;
        }

        if (Math.Abs(source.Length - candidate.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        var previousPrevious = new int[candidate.Length + 1];
        var previous = new int[candidate.Length + 1];
        var current = new int[candidate.Length + 1];

        for (var index = 0; index <= candidate.Length; index++)
        {
            previous[index] = index;
        }

        for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
        {
            current[0] = sourceIndex;
            var rowMinimum = current[0];

            for (var candidateIndex = 1; candidateIndex <= candidate.Length; candidateIndex++)
            {
                var substitutionCost = char.ToLowerInvariant(source[sourceIndex - 1]) ==
                                       char.ToLowerInvariant(candidate[candidateIndex - 1])
                    ? 0
                    : 1;

                var value = Math.Min(
                    Math.Min(
                        current[candidateIndex - 1] + 1,
                        previous[candidateIndex] + 1),
                    previous[candidateIndex - 1] + substitutionCost);

                if (sourceIndex > 1 &&
                    candidateIndex > 1 &&
                    char.ToLowerInvariant(source[sourceIndex - 1]) == char.ToLowerInvariant(candidate[candidateIndex - 2]) &&
                    char.ToLowerInvariant(source[sourceIndex - 2]) == char.ToLowerInvariant(candidate[candidateIndex - 1]))
                {
                    value = Math.Min(value, previousPrevious[candidateIndex - 2] + 1);
                }

                current[candidateIndex] = value;
                if (value < rowMinimum)
                {
                    rowMinimum = value;
                }
            }

            if (rowMinimum > maxDistance)
            {
                return maxDistance + 1;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[candidate.Length];
    }

    private static IReadOnlyDictionary<string, double> BuildTokenInverseDocumentFrequencies(
        IReadOnlyList<TokenSearchProfile> rawProfiles)
    {
        if (rawProfiles.Count == 0)
        {
            return EmptyTokenWeights;
        }

        var documentFrequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in rawProfiles)
        {
            foreach (var term in profile.Terms)
            {
                documentFrequencies[term] = documentFrequencies.TryGetValue(term, out var count)
                    ? count + 1
                    : 1;
            }
        }

        var inverseDocumentFrequencies = new Dictionary<string, double>(
            documentFrequencies.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (term, documentFrequency) in documentFrequencies)
        {
            inverseDocumentFrequencies[term] =
                1d + Math.Log((1d + rawProfiles.Count) / (1d + documentFrequency));
        }

        return inverseDocumentFrequencies;
    }

    private static TokenSearchProfile ApplyTokenInverseDocumentFrequencies(
        TokenSearchProfile rawProfile,
        IReadOnlyDictionary<string, double> inverseDocumentFrequencies)
    {
        if (rawProfile.TermWeights.Count == 0 || inverseDocumentFrequencies.Count == 0)
        {
            return rawProfile;
        }

        var weightedTerms = new Dictionary<string, double>(
            rawProfile.TermWeights.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (term, rawWeight) in rawProfile.TermWeights)
        {
            var inverseDocumentFrequency = inverseDocumentFrequencies.TryGetValue(term, out var value)
                ? value
                : 1d;
            weightedTerms[term] = rawWeight * inverseDocumentFrequency;
        }

        return CreateTokenSearchProfile(weightedTerms);
    }

}
