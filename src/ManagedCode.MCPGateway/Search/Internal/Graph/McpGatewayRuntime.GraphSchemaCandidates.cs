using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<SchemaGraphSearch> SearchGraphBySchemaAsync(
        ToolGraphSearchIndex graphIndex,
        string schemaQuery,
        KnowledgeGraphSchemaSearchProfile profile,
        string toolQuery,
        CancellationToken cancellationToken
    )
    {
        var exactNodeIds = graphIndex.EntriesByNodeId
            .Where(entry => string.Equals(entry.Value.Descriptor.ToolId, toolQuery, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Value.Descriptor.ToolName, toolQuery, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (profile.CandidateNodeIds is null)
        {
            if (exactNodeIds.Count > 0)
            {
                profile = profile with { CandidateNodeIds = exactNodeIds };
            }
            else if (_graphSchemaSearchProfile is null && ShouldUseCandidateSchemaSearch(graphIndex))
            {
                profile = profile with
                {
                    CandidateNodeIds = SelectSchemaCandidateNodeIds(graphIndex, schemaQuery, profile.MaxResults, cancellationToken)
                };
            }
        }

        var result = await graphIndex.Graph
                .SearchBySchemaAsync(schemaQuery, profile, cancellationToken)
                .ConfigureAwait(false);
        return new SchemaGraphSearch(result, UsedExactIdentity: exactNodeIds.Count > 0);
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
