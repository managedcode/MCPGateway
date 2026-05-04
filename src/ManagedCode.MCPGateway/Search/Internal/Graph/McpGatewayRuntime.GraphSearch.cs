using System.Diagnostics;
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
        bool addUnavailableDiagnostic,
        bool addFailureDiagnostics,
        bool enableFuzzySchemaFallback,
        CancellationToken cancellationToken
    )
    {
        var graphRanked = await TryRankWithGraphAsync(
            snapshot,
            searchInput,
            limit,
            diagnostics,
            addUnavailableDiagnostic,
            addFailureDiagnostics,
            enableFuzzySchemaFallback,
            cancellationToken
        );
        if (graphRanked is not null)
        {
            if (addGraphFallbackDiagnostic)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(GraphFallbackDiagnosticCode, GraphFallbackMessage)
                );
            }

            return graphRanked;
        }

        return new RankedSearch([], SearchModeGraph);
    }

    private async Task<RankedSearch?> TryRankWithGraphAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        bool addUnavailableDiagnostic,
        bool addFailureDiagnostics,
        bool enableFuzzySchemaFallback,
        CancellationToken cancellationToken
    )
    {
        if (snapshot.GraphIndex?.CanSearch != true)
        {
            if (addUnavailableDiagnostic)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        GraphUnavailableDiagnosticCode,
                        GraphUnavailableMessage
                    )
                );
            }

            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var rankedSearch = await RankWithGraphAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                enableFuzzySchemaFallback,
                cancellationToken
            );
            return rankedSearch with
            {
                Metrics = new RankedSearchMetrics(
                    UsedVectorSearch: rankedSearch.Metrics?.UsedVectorSearch ?? false,
                    UsedGraphSearch: true,
                    VectorDurationMilliseconds: rankedSearch.Metrics?.VectorDurationMilliseconds,
                    GraphDurationMilliseconds: stopwatch.Elapsed.TotalMilliseconds
                ),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (addFailureDiagnostics)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        GraphSearchFailedDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            GraphSearchFailedMessageFormat,
                            ex.GetBaseException().Message
                        )
                    )
                );
            }

            _logger.LogWarning(ex, GatewayGraphSearchFailedLogMessage);
            return null;
        }
    }

    private async Task<RankedSearch> RankWithGraphAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        bool enableFuzzySchemaFallback,
        CancellationToken cancellationToken
    )
    {
        var graphIndex =
            snapshot.GraphIndex ?? throw new InvalidOperationException(GraphUnavailableMessage);
        var candidateLimit = CalculateGraphSearchCandidateLimit(limit, snapshot.Entries.Count);
        var focusedSearch = await SearchFocusedGraphAsync(
            graphIndex,
            searchInput.GraphQuery,
            searchInput.SchemaQuery,
            candidateLimit,
            diagnostics,
            enableFuzzySchemaFallback,
            cancellationToken
        );

        var scoreContext = CreateSearchScoreContext(searchInput.BoostQuery);
        var primary = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.PrimaryMatches,
            scoreContext,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );
        var primaryToolIds = primary
            .Take(limit)
            .Select(static item => item.Entry.Descriptor.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.RelatedMatches,
            scoreContext,
            primaryToolIds
        );
        var nextSteps = MapFocusedGraphMatches(
            graphIndex,
            focusedSearch.NextStepMatches,
            scoreContext,
            primaryToolIds
        );

        return new RankedSearch(primary, SearchModeGraph)
        {
            Related = related,
            NextSteps = nextSteps,
            FocusedGraphNodeCount = focusedSearch.FocusedGraph.Nodes.Count,
            FocusedGraphEdgeCount = focusedSearch.FocusedGraph.Edges.Count,
            UsedSchemaSearch = focusedSearch.UsedSchemaSearch,
            UsedSchemaFallback = focusedSearch.UsedSchemaFallback,
        };
    }

    private static int CalculateGraphSearchCandidateLimit(int limit, int rankedCount) =>
        Math.Min(
            rankedCount,
            Math.Max(
                limit * GraphSearchCandidateMultiplier,
                GraphSearchMinimumCandidateWindow
            )
        );

    private async Task<FocusedGraphSearch> SearchFocusedGraphAsync(
        ToolGraphSearchIndex graphIndex,
        string query,
        string schemaQuery,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        bool enableFuzzySchemaFallback,
        CancellationToken cancellationToken
    )
    {
        if (_markdownLdGraphSearchMode == McpGatewayMarkdownLdGraphSearchMode.TokenDistance)
        {
            return await SearchFocusedGraphByTokenDistanceAsync(
                graphIndex,
                query,
                limit,
                cancellationToken
            );
        }

        var profile = CreateToolGraphSchemaSearchProfile(
            limit,
            GraphFocusedRelatedResultsLimit,
            GraphFocusedNextStepResultsLimit
        );
        AddSchemaProfileDiagnostics(graphIndex.SchemaDiagnostics, diagnostics);
        var schemaSearch = await SearchGraphBySchemaAsync(
                graphIndex,
                schemaQuery,
                profile,
                cancellationToken
            )
            .ConfigureAwait(false);
        var schemaFocusedSearch = CreateFocusedGraphSearchFromSchema(graphIndex, schemaSearch);

        if (_markdownLdGraphSearchMode == McpGatewayMarkdownLdGraphSearchMode.SchemaAware)
        {
            return schemaFocusedSearch;
        }

        if (HasMappedPrimaryGraphMatch(graphIndex, schemaSearch.Result.Matches))
        {
            if (!CanUseRankedBm25GraphSearch(graphIndex))
            {
                return schemaFocusedSearch;
            }

            var rankedFocusedSearch = SearchFocusedGraphByRankedBm25(
                    graphIndex,
                    query,
                    limit,
                    enableFuzzyTokenMatching: false,
                    cancellationToken
                );
            return MergeHybridFocusedGraphSearch(rankedFocusedSearch, schemaSearch.Result);
        }

        diagnostics.Add(
            new McpGatewayDiagnostic(
                GraphSchemaFallbackDiagnosticCode,
                ResolveGraphSchemaFallbackMessage(graphIndex, enableFuzzySchemaFallback)
            )
        );
        if (!enableFuzzySchemaFallback)
        {
            return new FocusedGraphSearch([], [], [], schemaFocusedSearch.FocusedGraph, true)
            {
                UsedSchemaFallback = true,
            };
        }

        if (!CanUseRankedBm25GraphSearch(graphIndex))
        {
            if (!graphIndex.CanSearchByTokenDistance)
            {
                return new FocusedGraphSearch([], [], [], schemaFocusedSearch.FocusedGraph, true)
                {
                    UsedSchemaFallback = true,
                };
            }

            var tokenDistanceFallback = await SearchFocusedGraphByTokenDistanceAsync(
                    graphIndex,
                    query,
                    limit,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return tokenDistanceFallback with { UsedSchemaSearch = true, UsedSchemaFallback = true };
        }

        var fallback = SearchFocusedGraphByRankedBm25(
                graphIndex,
                query,
                limit,
                enableFuzzyTokenMatching: true,
                cancellationToken
            );
        if (fallback.PrimaryMatches.Count > 0 || !graphIndex.CanSearchByTokenDistance)
        {
            return fallback with { UsedSchemaSearch = true, UsedSchemaFallback = true };
        }

        var rankedEmptyTokenFallback = await SearchFocusedGraphByTokenDistanceAsync(
                graphIndex,
                query,
                limit,
                cancellationToken
            )
            .ConfigureAwait(false);
        return rankedEmptyTokenFallback with { UsedSchemaSearch = true, UsedSchemaFallback = true };
    }

    private static async Task<FocusedGraphSearch> SearchFocusedGraphByTokenDistanceAsync(
        ToolGraphSearchIndex graphIndex,
        string query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var focusedSearch = await graphIndex
            .Graph.SearchFocusedAsync(
                query,
                CreateFocusedGraphSearchOptions(limit, schemaSearchProfile: null),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new FocusedGraphSearch(focusedSearch, usedSchemaSearch: false);
    }

    private static KnowledgeGraphFocusedSearchOptions CreateFocusedGraphSearchOptions(
        int limit,
        KnowledgeGraphSchemaSearchProfile? schemaSearchProfile
    ) =>
        new()
        {
            MaxPrimaryResults = limit,
            MaxRelatedResults = GraphFocusedRelatedResultsLimit,
            MaxNextStepResults = GraphFocusedNextStepResultsLimit,
            SchemaSearchProfile = schemaSearchProfile,
        };

    private static bool HasMappedPrimaryGraphMatch(
        ToolGraphSearchIndex graphIndex,
        IEnumerable<KnowledgeGraphSchemaSearchMatch> matches
    )
    {
        foreach (var match in matches)
        {
            if (TryResolveGraphToolEntry(graphIndex, match, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static FocusedGraphSearch MergeHybridFocusedGraphSearch(
        FocusedGraphSearch rankedSupportSearch,
        KnowledgeGraphSchemaSearchResult schemaSearch
    )
    {
        var focusedGraph =
            schemaSearch.FocusedGraph.Nodes.Count >= rankedSupportSearch.FocusedGraph.Nodes.Count
                ? schemaSearch.FocusedGraph
                : rankedSupportSearch.FocusedGraph;

        return new FocusedGraphSearch(
            MergeFocusedGraphMatches(
                ToFocusedGraphMatches(schemaSearch.Matches),
                rankedSupportSearch.PrimaryMatches
            ),
            MergeFocusedGraphMatches(
                ToFocusedGraphMatches(schemaSearch.RelatedMatches),
                rankedSupportSearch.RelatedMatches
            ),
            MergeFocusedGraphMatches(
                ToFocusedGraphMatches(schemaSearch.NextStepMatches),
                rankedSupportSearch.NextStepMatches
            ),
            focusedGraph,
            true
        );
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> MergeFocusedGraphMatches(
        IEnumerable<KnowledgeGraphFocusedSearchMatch> primaryMatches,
        IEnumerable<KnowledgeGraphFocusedSearchMatch> supplementaryMatches
    )
    {
        var matches = new List<KnowledgeGraphFocusedSearchMatch>();
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in primaryMatches)
        {
            if (seenNodeIds.Add(match.NodeId))
            {
                matches.Add(match);
            }
        }

        foreach (var match in supplementaryMatches)
        {
            if (seenNodeIds.Add(match.NodeId))
            {
                matches.Add(match);
            }
        }

        return matches;
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> ToFocusedGraphMatches(
        IEnumerable<KnowledgeGraphSchemaSearchMatch> matches
    )
    {
        var focusedMatches = new List<KnowledgeGraphFocusedSearchMatch>();
        foreach (var match in matches)
        {
            focusedMatches.Add(ToFocusedGraphMatch(match));
        }

        return focusedMatches;
    }

    private static KnowledgeGraphFocusedSearchMatch ToFocusedGraphMatch(
        KnowledgeGraphSchemaSearchMatch match
    ) =>
        new(
            match.NodeId,
            match.Label,
            match.Role switch
            {
                KnowledgeGraphSchemaSearchRole.Related => KnowledgeGraphFocusedSearchRole.Related,
                KnowledgeGraphSchemaSearchRole.NextStep => KnowledgeGraphFocusedSearchRole.NextStep,
                _ => KnowledgeGraphFocusedSearchRole.Primary,
            },
            match.Score,
            match.SourceNodeId,
            match.ViaPredicateId
        );

    private static IReadOnlyList<ScoredToolEntry> MapFocusedGraphMatches(
        ToolGraphSearchIndex graphIndex,
        IEnumerable<KnowledgeGraphFocusedSearchMatch> matches,
        SearchScoreContext scoreContext,
        IReadOnlySet<string> excludedToolIds
    )
    {
        var bestScores = new Dictionary<string, ScoredToolEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (
                !graphIndex.EntriesByNodeId.TryGetValue(match.NodeId, out var entry)
                || excludedToolIds.Contains(entry.Descriptor.ToolId)
            )
            {
                continue;
            }

            var score = CalibrateGraphConfidence(
                entry,
                scoreContext,
                ApplySearchBoosts(entry, scoreContext, Math.Clamp(match.Score, 0d, 1d))
            );
            if (
                !bestScores.TryGetValue(entry.Descriptor.ToolId, out var existing)
                || score > existing.Score
            )
            {
                bestScores[entry.Descriptor.ToolId] = new ScoredToolEntry(entry, score);
            }
        }

        var mappedMatches = bestScores.Values.ToList();
        mappedMatches.Sort(CompareScoredToolEntries);
        return mappedMatches;
    }

    private sealed record RankedSearch(IReadOnlyList<ScoredToolEntry> Ranked, string RankingMode)
    {
        public IReadOnlyList<ScoredToolEntry> Related { get; init; } = [];

        public IReadOnlyList<ScoredToolEntry> NextSteps { get; init; } = [];

        public int FocusedGraphNodeCount { get; init; }

        public int FocusedGraphEdgeCount { get; init; }

        public bool UsedSchemaSearch { get; init; }

        public bool UsedSchemaFallback { get; init; }

        public RankedSearchMetrics? Metrics { get; init; }
    }

    private sealed record SchemaGraphSearch(
        KnowledgeGraphSchemaSearchResult Result,
        bool UsedCandidateGraph
    );

    private sealed record FocusedGraphSearch(
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> PrimaryMatches,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> RelatedMatches,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> NextStepMatches,
        KnowledgeGraphSnapshot FocusedGraph,
        bool UsedSchemaSearch
    )
    {
        public FocusedGraphSearch(
            KnowledgeGraphFocusedSearchResult result,
            bool usedSchemaSearch
        )
            : this(
                result.PrimaryMatches,
                result.RelatedMatches,
                result.NextStepMatches,
                result.FocusedGraph,
                usedSchemaSearch
            )
        {
        }

        public FocusedGraphSearch(
            KnowledgeGraphSchemaSearchResult result,
            bool usedSchemaSearch
        )
            : this(
                ToFocusedGraphMatches(result.Matches),
                ToFocusedGraphMatches(result.RelatedMatches),
                ToFocusedGraphMatches(result.NextStepMatches),
                result.FocusedGraph,
                usedSchemaSearch
            )
        {
        }

        public bool UsedSchemaFallback { get; init; }
    }
}
