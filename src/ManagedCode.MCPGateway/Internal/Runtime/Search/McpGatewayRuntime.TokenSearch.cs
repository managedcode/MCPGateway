namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static double ApplySearchBoosts(ToolCatalogEntry entry, string query, double score)
        => Math.Clamp(score + (CalculateToolNameSignal(entry, query, TokenSearchProfile.Empty) * ToolNameSignalWeight), 0d, 1d);

    private IReadOnlyList<ScoredToolEntry> RankLexically(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput)
    {
        var querySegments = BuildQueryTokenSearchSegments(searchInput);
        var queryFields = BuildTokenizedSearchFields(querySegments);
        var rawQueryProfile = BuildTokenSearchProfile(queryFields);
        if (rawQueryProfile.TermWeights.Count == 0)
        {
            return RankHeuristicLexically(snapshot.Entries, searchInput.BoostQuery);
        }

        var queryProfile = ApplyTokenInverseDocumentFrequencies(
            rawQueryProfile,
            snapshot.TokenInverseDocumentFrequencies);
        var characterNGramProfile = BuildCharacterNGramProfile(
            queryFields,
            snapshot.CharacterNGramInverseDocumentFrequencies);
        var lexicalSearchTerms = BuildLexicalTerms(querySegments);
        var rawLexicalSearchTerms = BuildSearchTerms(searchInput.BoostQuery);
        var candidates = RetrieveCandidates(
            snapshot,
            queryProfile,
            rawQueryProfile,
            characterNGramProfile,
            rawLexicalSearchTerms,
            searchInput.BoostQuery);

        return candidates
            .Select(candidate => new ScoredToolEntry(
                candidate.Entry,
                CalculateTokenSearchScore(
                    candidate,
                    queryProfile,
                    rawQueryProfile,
                    lexicalSearchTerms,
                    searchInput.BoostQuery)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RetrievalCandidate> RetrieveCandidates(
        ToolCatalogSnapshot snapshot,
        TokenSearchProfile queryProfile,
        TokenSearchProfile rawQueryProfile,
        TokenSearchProfile characterNGramProfile,
        IReadOnlySet<string> rawLexicalSearchTerms,
        string rawQuery)
    {
        var retrievalCandidates = snapshot.Entries
            .Select(entry => new RetrievalCandidate(
                entry,
                CalculateBm25Score(
                    entry,
                    rawQueryProfile,
                    snapshot.TokenInverseDocumentFrequencies,
                    snapshot.AverageSearchFieldLength),
                CalculateSparseCosine(entry.TokenProfile, queryProfile),
                CalculateSparseCosine(entry.CharacterNGramProfile, characterNGramProfile),
                CalculateHeuristicLexicalScore(entry, rawQuery, rawLexicalSearchTerms)))
            .ToList();

        var bm25Ranks = BuildRankLookup(retrievalCandidates, static candidate => candidate.Bm25Score);
        var tokenRanks = BuildRankLookup(retrievalCandidates, static candidate => candidate.TokenSimilarity);
        var characterRanks = BuildRankLookup(retrievalCandidates, static candidate => candidate.CharacterNGramSimilarity);
        var heuristicRanks = BuildRankLookup(retrievalCandidates, static candidate => candidate.HeuristicLexicalScore);

        return retrievalCandidates
            .OrderByDescending(candidate =>
                CalculateReciprocalRankFusionScore(candidate.Entry, bm25Ranks, tokenRanks, characterRanks, heuristicRanks))
            .ThenByDescending(static candidate => candidate.Bm25Score)
            .ThenBy(static candidate => candidate.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .Take(ResolveCandidatePoolSize(retrievalCandidates.Count))
            .ToList();
    }

    private static int ResolveCandidatePoolSize(int candidateCount)
        => candidateCount <= 128
            ? candidateCount
            : Math.Min(StageOneCandidatePoolSize, candidateCount);

    private static IReadOnlyDictionary<string, int> BuildRankLookup(
        IReadOnlyList<RetrievalCandidate> candidates,
        Func<RetrievalCandidate, double> scoreSelector)
        => candidates
            .OrderByDescending(scoreSelector)
            .ThenBy(static candidate => candidate.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select((candidate, index) => new KeyValuePair<string, int>(candidate.Entry.Descriptor.ToolId, index + 1))
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase);

    private static double CalculateReciprocalRankFusionScore(
        ToolCatalogEntry entry,
        IReadOnlyDictionary<string, int> bm25Ranks,
        IReadOnlyDictionary<string, int> tokenRanks,
        IReadOnlyDictionary<string, int> characterRanks,
        IReadOnlyDictionary<string, int> heuristicRanks)
        => CalculateReciprocalRankComponent(entry, bm25Ranks)
            + CalculateReciprocalRankComponent(entry, tokenRanks)
            + CalculateReciprocalRankComponent(entry, characterRanks)
            + CalculateReciprocalRankComponent(entry, heuristicRanks);

    private static double CalculateReciprocalRankComponent(
        ToolCatalogEntry entry,
        IReadOnlyDictionary<string, int> ranks)
        => ranks.TryGetValue(entry.Descriptor.ToolId, out var rank)
            ? 1d / (ReciprocalRankFusionConstant + rank)
            : 0d;

    private static double CalculateTokenSearchScore(
        RetrievalCandidate candidate,
        TokenSearchProfile queryProfile,
        TokenSearchProfile rawQueryProfile,
        IReadOnlySet<string> lexicalSearchTerms,
        string rawQuery)
    {
        var bm25Score = NormalizePositiveScore(candidate.Bm25Score, 4d);
        var tokenCoverage = Math.Max(
            CalculateQueryCoverage(candidate.Entry.TokenProfile, queryProfile),
            CalculateQueryCoverage(candidate.Entry.TokenProfile, rawQueryProfile));
        var distinctCoverage = CalculateDistinctQueryCoverage(candidate.Entry, rawQueryProfile);
        var approximateCoverage = CalculateApproximateQueryCoverage(candidate.Entry, rawQueryProfile);
        var weightedApproximateCoverage = CalculateWeightedApproximateCoverage(candidate.Entry, queryProfile);
        var matchBreadth = Math.Max(Math.Max(distinctCoverage, approximateCoverage), weightedApproximateCoverage);
        var lexicalSimilarity = CalculateLexicalSimilarity(candidate.Entry, rawQuery, lexicalSearchTerms);
        var toolNameSignal = CalculateToolNameSignal(candidate.Entry, rawQuery, rawQueryProfile);

        var score =
            (bm25Score * Bm25FeatureWeight) +
            (candidate.TokenSimilarity * TokenSimilarityWeight) +
            (candidate.CharacterNGramSimilarity * CharacterNGramSimilarityWeight) +
            (tokenCoverage * TokenCoverageWeight) +
            (matchBreadth * DistinctCoverageWeight) +
            (lexicalSimilarity * LexicalSimilarityWeight) +
            (candidate.HeuristicLexicalScore * HeuristicLexicalFeatureWeight) +
            (toolNameSignal * ToolNameSignalWeight);

        var evidenceCalibration = Math.Max(tokenCoverage, matchBreadth);
        return Math.Clamp(score * evidenceCalibration, 0d, 1d);
    }

    private static double NormalizePositiveScore(double value, double scale)
        => value <= double.Epsilon
            ? 0d
            : 1d - Math.Exp(-value / scale);

    private static double CalculateBm25Score(
        ToolCatalogEntry entry,
        TokenSearchProfile queryProfile,
        IReadOnlyDictionary<string, double> inverseDocumentFrequencies,
        double averageFieldLength)
    {
        if (queryProfile.TermWeights.Count == 0)
        {
            return 0d;
        }

        var score = 0d;
        foreach (var (term, queryWeight) in queryProfile.TermWeights)
        {
            var fieldScore = 0d;
            foreach (var field in entry.SearchFields)
            {
                if (!field.TermWeights.TryGetValue(term, out var termFrequency) ||
                    termFrequency <= double.Epsilon)
                {
                    continue;
                }

                var normalizedLength =
                    1d - Bm25FieldLengthNormalization +
                    (Bm25FieldLengthNormalization * (Math.Max(1, field.Length) / Math.Max(1d, averageFieldLength)));
                var denominator = termFrequency + (Bm25K1 * normalizedLength);
                if (denominator <= double.Epsilon)
                {
                    continue;
                }

                fieldScore += field.Weight * ((termFrequency * (Bm25K1 + 1d)) / denominator);
            }

            if (fieldScore <= double.Epsilon)
            {
                continue;
            }

            var inverseDocumentFrequency = inverseDocumentFrequencies.TryGetValue(term, out var value)
                ? value
                : 1d;
            score += inverseDocumentFrequency * Math.Sqrt(queryWeight) * fieldScore;
        }

        return score;
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
            score += 1.5d;
        }

        return Math.Clamp(score / (searchTerms.Count + 1.5d), 0d, 1d);
    }

    private static double CalculateDistinctQueryCoverage(
        ToolCatalogEntry entry,
        TokenSearchProfile rawQueryProfile)
    {
        if (rawQueryProfile.TermWeights.Count == 0)
        {
            return 0d;
        }

        var matchedTerms = 0;
        foreach (var term in rawQueryProfile.TermWeights.Keys)
        {
            if (entry.LexicalTerms.Contains(term) ||
                entry.LexicalTerms.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                matchedTerms++;
            }
        }

        return matchedTerms == 0
            ? 0d
            : Math.Clamp(matchedTerms / (double)rawQueryProfile.TermWeights.Count, 0d, 1d);
    }

    private static double CalculateApproximateQueryCoverage(
        ToolCatalogEntry entry,
        TokenSearchProfile rawQueryProfile)
    {
        if (rawQueryProfile.TermWeights.Count == 0)
        {
            return 0d;
        }

        var matchedTerms = 0;
        foreach (var term in rawQueryProfile.TermWeights.Keys)
        {
            if (entry.LexicalTerms.Contains(term) ||
                entry.LexicalTerms.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) ||
                    CalculateApproximateTermSimilarity(term, candidate) > double.Epsilon))
            {
                matchedTerms++;
            }
        }

        return matchedTerms == 0
            ? 0d
            : Math.Clamp(matchedTerms / (double)rawQueryProfile.TermWeights.Count, 0d, 1d);
    }

    private static double CalculateWeightedApproximateCoverage(
        ToolCatalogEntry entry,
        TokenSearchProfile queryProfile)
    {
        if (queryProfile.TotalWeight <= double.Epsilon)
        {
            return 0d;
        }

        var matchedWeight = 0d;
        foreach (var (term, weight) in queryProfile.TermWeights)
        {
            if (entry.LexicalTerms.Contains(term) ||
                entry.LexicalTerms.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) ||
                    CalculateApproximateTermSimilarity(term, candidate) > double.Epsilon))
            {
                matchedWeight += weight;
            }
        }

        return matchedWeight <= double.Epsilon
            ? 0d
            : Math.Clamp(matchedWeight / queryProfile.TotalWeight, 0d, 1d);
    }

    private static double CalculateToolNameSignal(
        ToolCatalogEntry entry,
        string rawQuery,
        TokenSearchProfile rawQueryProfile)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return 0d;
        }

        if (string.Equals(entry.Descriptor.ToolName, rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1d;
        }

        var humanizedToolName = HumanizeIdentifier(entry.Descriptor.ToolName);
        if (humanizedToolName.Contains(rawQuery, StringComparison.OrdinalIgnoreCase) ||
            entry.Descriptor.ToolName.Contains(rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0.5d;
        }

        if (rawQueryProfile.TermWeights.Count == 0)
        {
            return 0d;
        }

        var toolNameTerms = BuildSearchTerms(entry.Descriptor.ToolName);
        if (toolNameTerms.Count == 0)
        {
            return 0d;
        }

        var matchedTerms = rawQueryProfile.TermWeights.Keys.Count(toolNameTerms.Contains);
        return matchedTerms == 0
            ? 0d
            : Math.Clamp(matchedTerms / (double)Math.Max(1, toolNameTerms.Count), 0d, 1d);
    }

    private static IReadOnlyList<ScoredToolEntry> RankHeuristicLexically(
        IReadOnlyList<ToolCatalogEntry> entries,
        string query)
    {
        var searchTerms = BuildSearchTerms(query);
        return entries
            .Select(entry => new ScoredToolEntry(entry, CalculateHeuristicLexicalScore(entry, query, searchTerms)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double CalculateHeuristicLexicalScore(
        ToolCatalogEntry entry,
        string query,
        IReadOnlySet<string> searchTerms)
    {
        var lexicalSimilarity = CalculateLexicalSimilarity(entry, query, searchTerms);
        var approximateTermSimilarity = CalculateApproximateTermSimilarity(entry, searchTerms);
        var score =
            (lexicalSimilarity * 0.7d) +
            (approximateTermSimilarity * 0.3d);
        return Math.Clamp(score, 0d, 1d);
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
