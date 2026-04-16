using System.Globalization;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<RankedSearch> RankWithGraphOrEmptyAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        bool addGraphFallbackDiagnostic,
        CancellationToken cancellationToken)
    {
        if (snapshot.GraphIndex?.CanSearch == true)
        {
            try
            {
                var graphRanked = await RankWithGraphAsync(snapshot, searchInput, limit, cancellationToken);
                if (addGraphFallbackDiagnostic)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(GraphFallbackDiagnosticCode, GraphFallbackMessage));
                }

                return graphRanked;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    GraphSearchFailedDiagnosticCode,
                    string.Format(CultureInfo.InvariantCulture, GraphSearchFailedMessageFormat, ex.GetBaseException().Message)));
                _logger.LogWarning(ex, GatewayGraphSearchFailedLogMessage);
            }
        }
        else
        {
            diagnostics.Add(new McpGatewayDiagnostic(GraphUnavailableDiagnosticCode, GraphUnavailableMessage));
        }

        return new RankedSearch([], SearchModeGraph);
    }

    private static async Task<RankedSearch> RankWithGraphAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        CancellationToken cancellationToken)
    {
        var graphIndex = snapshot.GraphIndex ?? throw new InvalidOperationException(GraphUnavailableMessage);
        var focusedSearch = await graphIndex.Graph.SearchFocusedAsync(
            searchInput.EffectiveQuery,
            new KnowledgeGraphFocusedSearchOptions
            {
                MaxPrimaryResults = limit,
                MaxRelatedResults = GraphFocusedRelatedResultsLimit,
                MaxNextStepResults = GraphFocusedNextStepResultsLimit
            },
            cancellationToken);

        var primary = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.PrimaryMatches,
            searchInput.BoostQuery,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var primaryToolIds = primary
            .Select(static item => item.Entry.Descriptor.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.RelatedMatches,
            searchInput.BoostQuery,
            primaryToolIds);
        var nextSteps = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.NextStepMatches,
            searchInput.BoostQuery,
            primaryToolIds);

        return new RankedSearch(primary, SearchModeGraph)
        {
            Related = related,
            NextSteps = nextSteps,
            FocusedGraphNodeCount = focusedSearch.FocusedGraph.Nodes.Count,
            FocusedGraphEdgeCount = focusedSearch.FocusedGraph.Edges.Count
        };
    }

    private static IReadOnlyList<ScoredToolEntry> MapFocusedGraphMatches(
        ToolGraphSearchIndex graphIndex,
        IEnumerable<KnowledgeGraphFocusedSearchMatch> matches,
        string boostQuery,
        IReadOnlySet<string> excludedToolIds)
    {
        var bestScores = new Dictionary<string, ScoredToolEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (!graphIndex.EntriesByNodeId.TryGetValue(match.NodeId, out var entry) ||
                excludedToolIds.Contains(entry.Descriptor.ToolId))
            {
                continue;
            }

            var score = CalibrateGraphConfidence(
                entry,
                boostQuery,
                ApplySearchBoosts(entry, boostQuery, Math.Clamp(match.Score, 0d, 1d)));
            if (!bestScores.TryGetValue(entry.Descriptor.ToolId, out var existing) || score > existing.Score)
            {
                bestScores[entry.Descriptor.ToolId] = new ScoredToolEntry(entry, score);
            }
        }

        return bestScores.Values
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record RankedSearch(IReadOnlyList<ScoredToolEntry> Ranked, string RankingMode)
    {
        public IReadOnlyList<ScoredToolEntry> Related { get; init; } = [];

        public IReadOnlyList<ScoredToolEntry> NextSteps { get; init; } = [];

        public int FocusedGraphNodeCount { get; init; }

        public int FocusedGraphEdgeCount { get; init; }
    }
}
