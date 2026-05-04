using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static GraphCandidateSearchIndex CreateGraphCandidateSearchIndex(
        IReadOnlyDictionary<string, ToolCatalogEntry> entriesByNodeId,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        GraphNavigationIndex navigation
    )
    {
        if (entriesByNodeId.Count == 0)
        {
            return GraphCandidateSearchIndex.Empty;
        }

        var candidates = new List<GraphCandidateSearchEntry>(entriesByNodeId.Count);
        foreach (var (nodeId, entry) in entriesByNodeId)
        {
            candidates.Add(CreateGraphCandidateSearchEntry(nodeId, entry, nodesById, navigation));
        }

        candidates.Sort(
            static (left, right) => string.Compare(left.NodeId, right.NodeId, StringComparison.Ordinal)
        );
        return new GraphCandidateSearchIndex(
            candidates.ToArray(),
            CreateGraphCandidateInverseDocumentFrequency(candidates)
        );
    }

    private static GraphCandidateSearchEntry CreateGraphCandidateSearchEntry(
        string nodeId,
        ToolCatalogEntry entry,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        GraphNavigationIndex navigation
    )
    {
        var terms = new HashSet<string>(entry.SearchBoostTerms, StringComparer.OrdinalIgnoreCase);
        if (nodesById.TryGetValue(nodeId, out var node))
        {
            AddGraphCandidateTerms(terms, node.Label);
        }

        AddGraphCandidateTerms(terms, entry.Descriptor.ToolName);
        AddGraphCandidateTerms(terms, entry.Descriptor.DisplayName);
        AddGraphCandidateTerms(terms, entry.Descriptor.Description);
        AddGraphCandidateEdgeTerms(terms, nodeId, nodesById, navigation);
        return new GraphCandidateSearchEntry(
            nodeId,
            node?.Label ?? entry.Descriptor.ToolName,
            entry.Descriptor.Description,
            terms
        );
    }

    private static void AddGraphCandidateEdgeTerms(
        ISet<string> terms,
        string nodeId,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        GraphNavigationIndex navigation
    )
    {
        if (!navigation.EdgesBySubject.TryGetValue(nodeId, out var edges))
        {
            return;
        }

        foreach (var edge in edges)
        {
            if (
                IsGraphCandidateTextPredicate(edge)
                && nodesById.TryGetValue(edge.ObjectId, out var valueNode)
            )
            {
                AddGraphCandidateTerms(terms, valueNode.Label);
            }
        }
    }

    private static bool IsGraphCandidateTextPredicate(KnowledgeGraphEdge edge) =>
        IsGraphPredicate(edge, SchemaPredicateName)
        || IsGraphPredicate(edge, SchemaPredicateDescription)
        || IsGraphPredicate(edge, SchemaPredicateKeywords)
        || IsGraphPredicate(edge, SkosPredicatePrefLabel)
        || IsGraphPredicate(edge, SchemaPredicateAbout)
        || IsGraphPredicate(edge, SchemaPredicateMentions)
        || IsGraphPredicate(edge, KbPredicateMemberOf);

    private static void AddGraphCandidateTerms(ISet<string> terms, string? value)
    {
        foreach (var term in BuildOrderedGraphTerms(HumanizeIdentifier(value ?? string.Empty)))
        {
            terms.Add(term);
        }
    }

    private static IReadOnlyDictionary<string, double> CreateGraphCandidateInverseDocumentFrequency(
        IReadOnlyList<GraphCandidateSearchEntry> candidates
    )
    {
        var documentFrequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            foreach (var term in candidate.Terms)
            {
                documentFrequencies[term] = documentFrequencies.GetValueOrDefault(term) + 1;
            }
        }

        var candidateCount = candidates.Count;
        var inverseDocumentFrequency = new Dictionary<string, double>(
            documentFrequencies.Count,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (term, frequency) in documentFrequencies)
        {
            inverseDocumentFrequency[term] =
                Math.Log(
                    (candidateCount + GraphCandidateIdfDocumentOffset)
                        / (frequency + GraphCandidateIdfFrequencyOffset)
                ) + GraphCandidateIdfBaseWeight;
        }

        return inverseDocumentFrequency;
    }

    private static IReadOnlyList<KnowledgeGraphRankedSearchMatch> SearchGraphCandidates(
        GraphCandidateSearchIndex index,
        string query,
        int limit,
        bool enableFuzzyTokenMatching
    )
    {
        if (index.Entries.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryTerms = BuildOrderedGraphTerms(query).Take(GraphSchemaQueryMaxTerms).ToArray();
        if (queryTerms.Length == 0)
        {
            return [];
        }

        var maximumScore = CalculateMaximumGraphCandidateScore(
            queryTerms,
            index.InverseDocumentFrequency
        );
        if (maximumScore <= SearchScoreMinimum)
        {
            return [];
        }

        var matches = new List<KnowledgeGraphRankedSearchMatch>();
        foreach (var candidate in index.Entries)
        {
            var score = ScoreGraphCandidate(
                candidate,
                queryTerms,
                index.InverseDocumentFrequency,
                enableFuzzyTokenMatching
            );
            if (score <= SearchScoreMinimum)
            {
                continue;
            }

            var normalizedScore = Math.Clamp(
                score / maximumScore,
                SearchScoreMinimum,
                SearchScoreMaximum
            );
            matches.Add(
                new KnowledgeGraphRankedSearchMatch(
                    candidate.NodeId,
                    candidate.Label,
                    candidate.Description,
                    KnowledgeGraphRankedSearchSource.Bm25,
                    normalizedScore,
                    CanonicalScore: normalizedScore
                )
            );
        }

        matches.Sort(CompareGraphCandidateMatches);
        if (matches.Count > limit)
        {
            matches.RemoveRange(limit, matches.Count - limit);
        }

        return matches.ToArray();
    }

    private static double CalculateMaximumGraphCandidateScore(
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, double> inverseDocumentFrequency
    )
    {
        var maximumScore = SearchScoreMinimum;
        foreach (var term in queryTerms)
        {
            maximumScore += ResolveGraphCandidateTermWeight(term, inverseDocumentFrequency);
        }

        return maximumScore;
    }

    private static double ScoreGraphCandidate(
        GraphCandidateSearchEntry candidate,
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, double> inverseDocumentFrequency,
        bool enableFuzzyTokenMatching
    )
    {
        var score = SearchScoreMinimum;
        foreach (var term in queryTerms)
        {
            if (candidate.Terms.Contains(term))
            {
                score += ResolveGraphCandidateTermWeight(term, inverseDocumentFrequency);
                continue;
            }

            if (enableFuzzyTokenMatching && HasFuzzyGraphCandidateTerm(candidate.Terms, term))
            {
                score +=
                    ResolveGraphCandidateTermWeight(term, inverseDocumentFrequency)
                    * GraphCandidateFuzzyScoreMultiplier;
            }
        }

        return score;
    }

    private static double ResolveGraphCandidateTermWeight(
        string term,
        IReadOnlyDictionary<string, double> inverseDocumentFrequency
    ) =>
        inverseDocumentFrequency.TryGetValue(term, out var weight)
            ? weight
            : GraphCandidateDefaultTermWeight;

    private static bool HasFuzzyGraphCandidateTerm(IEnumerable<string> candidateTerms, string term)
    {
        if (term.Length < GraphMinimumFuzzyTermLength)
        {
            return false;
        }

        foreach (var candidateTerm in candidateTerms)
        {
            if (
                candidateTerm.Length >= GraphMinimumFuzzyTermLength
                && IsGraphCandidateEditDistanceAtMostOne(term, candidateTerm)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGraphCandidateEditDistanceAtMostOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > GraphRankedSearchMaxFuzzyEditDistance)
        {
            return false;
        }

        var leftIndex = 0;
        var rightIndex = 0;
        var edits = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            edits++;
            if (edits > GraphRankedSearchMaxFuzzyEditDistance)
            {
                return false;
            }

            if (left.Length > right.Length)
            {
                leftIndex++;
            }
            else if (right.Length > left.Length)
            {
                rightIndex++;
            }
            else
            {
                leftIndex++;
                rightIndex++;
            }
        }

        return edits + Math.Max(left.Length - leftIndex, right.Length - rightIndex)
            <= GraphRankedSearchMaxFuzzyEditDistance;
    }

    private static int CompareGraphCandidateMatches(
        KnowledgeGraphRankedSearchMatch left,
        KnowledgeGraphRankedSearchMatch right
    )
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        return scoreComparison != 0
            ? scoreComparison
            : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
    }
}
