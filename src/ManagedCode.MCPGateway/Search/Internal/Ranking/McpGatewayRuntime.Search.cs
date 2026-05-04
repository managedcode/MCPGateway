using System.Diagnostics;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var descriptors = new List<McpGatewayToolDescriptor>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            descriptors.Add(entry.Descriptor);
        }

        return descriptors;
    }

    public async Task<McpGatewaySearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        CancellationToken cancellationToken = default
    ) =>
        await SearchAsync(
            new McpGatewaySearchRequest(Query: query, MaxResults: maxResults),
            cancellationToken
        );

    public async Task<McpGatewaySearchResult> SearchAsync(
        McpGatewaySearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = McpGatewayTelemetry.StartSearchActivity(_searchStrategy);
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var diagnostics = new List<McpGatewayDiagnostic>();
        var limit = Math.Clamp(
            request.MaxResults.GetValueOrDefault(_defaultSearchLimit),
            1,
            _maxSearchResults
        );
        var cacheHit = false;
        var queryNormalized = false;
        RankedSearchMetrics? rankedMetrics = null;
        McpGatewaySearchResult result;
        var originalQuery = NormalizeSearchComponent(request.Query);
        var contextSummary = NormalizeSearchComponent(request.ContextSummary);
        var flattenedContext = NormalizeSearchComponent(FlattenContext(request.Context));

        if (snapshot.Entries.Count == 0)
        {
            result = new McpGatewaySearchResult([], diagnostics, SearchModeEmpty);
        }
        else
        {
            var normalizedQuery = await NormalizeSearchQueryAsync(
                originalQuery,
                diagnostics,
                cancellationToken
            );
            queryNormalized = normalizedQuery is not null;
            var searchInput = BuildSearchInput(
                originalQuery,
                NormalizeSearchComponent(normalizedQuery),
                contextSummary,
                flattenedContext
            );
            var chatClientFingerprint = Volatile.Read(ref _searchQueryChatClientFingerprint);
            var embeddingGeneratorFingerprint =
                await ResolveSearchResultEmbeddingGeneratorFingerprintAsync(
                    snapshot,
                    searchInput,
                    cancellationToken
                );

            var cachedSearchResult = await _searchRuntimeCache.TryGetSearchResultAsync(
                snapshot.Version,
                _searchStrategy,
                _searchQueryNormalization,
                originalQuery,
                contextSummary,
                flattenedContext,
                request.IncludeDisabledTools,
                limit,
                chatClientFingerprint,
                embeddingGeneratorFingerprint,
                cancellationToken
            );
            if (cachedSearchResult.found && cachedSearchResult.result is not null)
            {
                result = cachedSearchResult.result.Result;
                cacheHit = true;
                queryNormalized = cachedSearchResult.result.QueryNormalized;
            }
            else
            {
                if (!searchInput.HasTerms)
                {
                    var browse = CreateBrowseSearchMatches(
                        snapshot.Entries,
                        request.IncludeDisabledTools,
                        limit
                    );
                    result = new McpGatewaySearchResult(browse, diagnostics, SearchModeBrowse);
                }
                else
                {
                    var rankedSearch = await RankWithConfiguredStrategyAsync(
                        snapshot,
                        searchInput,
                        limit,
                        diagnostics,
                        cancellationToken
                    );

                    var matches = MapScoredSearchMatches(
                        rankedSearch.Ranked,
                        request.IncludeDisabledTools,
                        limit
                    );
                    var relatedMatches = MapScoredSearchMatches(
                        rankedSearch.Related,
                        request.IncludeDisabledTools,
                        limit
                    );
                    var nextStepMatches = MapScoredSearchMatches(
                        rankedSearch.NextSteps,
                        request.IncludeDisabledTools,
                        limit
                    );

                    rankedMetrics = rankedSearch.Metrics;
                    result = new McpGatewaySearchResult(
                        matches,
                        diagnostics,
                        rankedSearch.RankingMode
                    )
                    {
                        RelatedMatches = relatedMatches,
                        NextStepMatches = nextStepMatches,
                        FocusedGraphNodeCount = rankedSearch.FocusedGraphNodeCount,
                        FocusedGraphEdgeCount = rankedSearch.FocusedGraphEdgeCount,
                        UsedSchemaSearch = rankedSearch.UsedSchemaSearch,
                        UsedSchemaFallback = rankedSearch.UsedSchemaFallback,
                    };
                }

                await _searchRuntimeCache.SetSearchResultAsync(
                    snapshot.Version,
                    _searchStrategy,
                    _searchQueryNormalization,
                    originalQuery,
                    contextSummary,
                    flattenedContext,
                    request.IncludeDisabledTools,
                    limit,
                    chatClientFingerprint,
                    embeddingGeneratorFingerprint,
                    new McpGatewaySearchCachedResult(result, queryNormalized),
                    cancellationToken
                );
            }
        }

        McpGatewayTelemetry.RecordSearch(
            activity,
            _searchStrategy,
            result,
            rankedMetrics,
            cacheHit,
            queryNormalized,
            durationMilliseconds: stopwatch.Elapsed.TotalMilliseconds
        );
        return result;
    }

    private static IReadOnlyList<McpGatewaySearchMatch> CreateBrowseSearchMatches(
        IReadOnlyList<ToolCatalogEntry> entries,
        bool includeDisabledTools,
        int limit
    )
    {
        var matches = new List<McpGatewaySearchMatch>(Math.Min(entries.Count, limit));
        foreach (var entry in entries)
        {
            if (!includeDisabledTools && !entry.Descriptor.IsEnabledByDefault)
            {
                continue;
            }

            matches.Add(ToSearchMatch(entry, SearchScoreMinimum));
            if (matches.Count >= limit)
            {
                break;
            }
        }

        return matches;
    }

    private static IReadOnlyList<McpGatewaySearchMatch> MapScoredSearchMatches(
        IReadOnlyList<ScoredToolEntry> scoredEntries,
        bool includeDisabledTools,
        int limit
    )
    {
        var matches = new List<McpGatewaySearchMatch>(Math.Min(scoredEntries.Count, limit));
        foreach (var scoredEntry in scoredEntries)
        {
            if (!includeDisabledTools && !scoredEntry.Entry.Descriptor.IsEnabledByDefault)
            {
                continue;
            }

            matches.Add(ToSearchMatch(scoredEntry.Entry, scoredEntry.Score));
            if (matches.Count >= limit)
            {
                break;
            }
        }

        return matches;
    }

    private static McpGatewaySearchMatch ToSearchMatch(ToolCatalogEntry entry, double score) =>
        new(
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.SourceKind,
            entry.Descriptor.ToolName,
            entry.Descriptor.DisplayName,
            entry.Descriptor.Description,
            entry.Descriptor.RequiredArguments,
            entry.Descriptor.InputSchemaJson,
            Math.Clamp(score, SearchScoreMinimum, SearchScoreMaximum)
        )
        {
            SearchAliases = entry.Descriptor.SearchAliases,
            SearchKeywords = entry.Descriptor.SearchKeywords,
            Categories = entry.Descriptor.Categories,
            Tags = entry.Descriptor.Tags,
            DataSources = entry.Descriptor.DataSources,
            UsageExamples = entry.Descriptor.UsageExamples,
            IsReadOnly = entry.Descriptor.IsReadOnly,
            IsIdempotent = entry.Descriptor.IsIdempotent,
            IsDestructive = entry.Descriptor.IsDestructive,
            IsOpenWorld = entry.Descriptor.IsOpenWorld,
            CostTier = entry.Descriptor.CostTier,
            LatencyTier = entry.Descriptor.LatencyTier,
            IsEnabledByDefault = entry.Descriptor.IsEnabledByDefault,
        };
}
