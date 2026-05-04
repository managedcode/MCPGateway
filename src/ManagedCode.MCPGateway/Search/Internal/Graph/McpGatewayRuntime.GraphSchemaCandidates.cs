using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static async Task<SchemaGraphSearch> SearchGraphBySchemaAsync(
        ToolGraphSearchIndex graphIndex,
        string schemaQuery,
        KnowledgeGraphSchemaSearchProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (!ShouldUseCandidateSchemaSearch(graphIndex))
        {
            var result = await graphIndex
                    .Graph.SearchBySchemaAsync(schemaQuery, profile, cancellationToken)
                    .ConfigureAwait(false);
            return new SchemaGraphSearch(result, UsedCandidateGraph: false);
        }

        var candidateNodeIds = SelectSchemaCandidateNodeIds(
            graphIndex,
            schemaQuery,
            profile.MaxResults,
            cancellationToken
        );
        var candidateSnapshot = BuildSchemaCandidateGraphSnapshot(graphIndex, candidateNodeIds);
        using var candidateGraph = KnowledgeGraph.FromSnapshot(candidateSnapshot);
        var candidateResult = await candidateGraph
                .SearchBySchemaAsync(schemaQuery, profile, cancellationToken)
                .ConfigureAwait(false);
        return new SchemaGraphSearch(candidateResult, UsedCandidateGraph: true);
    }

    private static FocusedGraphSearch CreateFocusedGraphSearchFromSchema(
        ToolGraphSearchIndex graphIndex,
        SchemaGraphSearch schemaSearch
    )
    {
        if (!schemaSearch.UsedCandidateGraph)
        {
            return new FocusedGraphSearch(schemaSearch.Result, usedSchemaSearch: true);
        }

        var primary = ToFocusedGraphMatches(schemaSearch.Result.Matches);
        var related = ResolveRankedRelatedMatches(graphIndex, primary);
        var nextSteps = ResolveRankedNextStepMatches(graphIndex, primary);
        var focusedGraph = BuildRankedFocusedGraph(graphIndex, primary, related, nextSteps);
        return new FocusedGraphSearch(primary, related, nextSteps, focusedGraph, true);
    }

    private static bool ShouldUseCandidateSchemaSearch(ToolGraphSearchIndex graphIndex) =>
        graphIndex.SearchableNodeIds.Count > GraphCandidateSchemaSearchCatalogThreshold;

    private static IReadOnlySet<string> SelectSchemaCandidateNodeIds(
        ToolGraphSearchIndex graphIndex,
        string schemaQuery,
        int limit,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rankedMatches = SearchGraphCandidates(
            graphIndex.CandidateSearch,
            schemaQuery,
            CalculateSchemaCandidateLimit(limit, graphIndex.SearchableNodeIds.Count),
            enableFuzzyTokenMatching: false
        );
        if (rankedMatches.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var candidateNodeIds = new HashSet<string>(rankedMatches.Count, StringComparer.Ordinal);
        foreach (var rankedMatch in rankedMatches)
        {
            if (graphIndex.SearchableNodeIds.Contains(rankedMatch.NodeId))
            {
                candidateNodeIds.Add(rankedMatch.NodeId);
            }
        }

        return candidateNodeIds;
    }

    private static int CalculateSchemaCandidateLimit(int limit, int rankedCount) =>
        Math.Min(rankedCount, Math.Max(limit, GraphSchemaCandidateMinimumWindow));

    private static KnowledgeGraphSnapshot BuildSchemaCandidateGraphSnapshot(
        ToolGraphSearchIndex graphIndex,
        IReadOnlySet<string> candidateNodeIds
    )
    {
        if (candidateNodeIds.Count == 0)
        {
            return KnowledgeGraphSnapshot.Empty;
        }

        var subjectIds = new HashSet<string>(candidateNodeIds, StringComparer.Ordinal);
        foreach (var candidateNodeId in candidateNodeIds)
        {
            if (
                !graphIndex.Navigation.EdgesBySubject.TryGetValue(
                    candidateNodeId,
                    out var candidateEdges
                )
            )
            {
                continue;
            }

            foreach (var edge in candidateEdges)
            {
                if (!graphIndex.SearchableNodeIds.Contains(edge.ObjectId))
                {
                    subjectIds.Add(edge.ObjectId);
                }
            }
        }

        var edges = new List<KnowledgeGraphEdge>();
        foreach (var subjectId in subjectIds)
        {
            if (!graphIndex.Navigation.EdgesBySubject.TryGetValue(subjectId, out var subjectEdges))
            {
                continue;
            }

            foreach (var edge in subjectEdges)
            {
                if (
                    !graphIndex.SearchableNodeIds.Contains(edge.ObjectId)
                    || candidateNodeIds.Contains(edge.ObjectId)
                )
                {
                    edges.Add(edge);
                }
            }
        }

        var nodeIds = new HashSet<string>(subjectIds, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            nodeIds.Add(edge.SubjectId);
            nodeIds.Add(edge.ObjectId);
        }

        var nodes = new List<KnowledgeGraphNode>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (graphIndex.NodesById.TryGetValue(nodeId, out var node))
            {
                nodes.Add(node);
            }
        }

        return new KnowledgeGraphSnapshot(nodes, edges);
    }

    private static bool CanUseRankedCandidateGraphSearch(ToolGraphSearchIndex graphIndex) =>
        graphIndex.CandidateSearch.Entries.Count > 0;

    private static string ResolveGraphSchemaFallbackMessage(
        ToolGraphSearchIndex graphIndex,
        bool enableFuzzySchemaFallback
    )
    {
        if (!enableFuzzySchemaFallback)
        {
            return GraphSchemaNoSupplementMessage;
        }

        return CanUseRankedCandidateGraphSearch(graphIndex)
            ? GraphSchemaFallbackMessage
            : GraphSchemaTokenDistanceFallbackMessage;
    }
}
