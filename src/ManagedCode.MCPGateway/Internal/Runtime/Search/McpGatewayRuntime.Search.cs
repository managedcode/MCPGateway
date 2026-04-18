using System.Diagnostics;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Entries
            .Select(static item => item.Descriptor)
            .ToList();
    }

    public async Task<McpGatewaySearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
        => await SearchAsync(
            new McpGatewaySearchRequest(
                Query: query,
                MaxResults: maxResults),
            cancellationToken);

    public async Task<McpGatewaySearchResult> SearchAsync(
        McpGatewaySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = McpGatewayTelemetry.StartSearchActivity(_searchStrategy);
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var diagnostics = new List<McpGatewayDiagnostic>();
        var limit = Math.Clamp(request.MaxResults.GetValueOrDefault(_defaultSearchLimit), 1, _maxSearchResults);
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
        else if (_searchRuntimeCache.TryGetSearchResult(
                     snapshot.Version,
                     _searchStrategy,
                     originalQuery,
                     contextSummary,
                     flattenedContext,
                     limit,
                     out var cachedSearchResult))
        {
            result = cachedSearchResult.Result;
            rankedMetrics = cachedSearchResult.Metrics;
            cacheHit = true;
            queryNormalized = cachedSearchResult.QueryNormalized;
        }
        else
        {
            var normalizedQuery = await NormalizeSearchQueryAsync(originalQuery, diagnostics, cancellationToken);
            queryNormalized = normalizedQuery is not null;
            var searchInput = BuildSearchInput(
                originalQuery,
                NormalizeSearchComponent(normalizedQuery),
                contextSummary,
                flattenedContext);
            if (!searchInput.HasTerms)
            {
                var browse = snapshot.Entries
                    .Take(limit)
                    .Select(static entry => ToSearchMatch(entry, 0d))
                    .ToList();
                result = new McpGatewaySearchResult(browse, diagnostics, SearchModeBrowse);
            }
            else
            {
                var rankedSearch = await RankWithConfiguredStrategyAsync(
                    snapshot,
                    searchInput,
                    limit,
                    diagnostics,
                    cancellationToken);

                var matches = rankedSearch.Ranked
                    .Take(limit)
                    .Select(item => ToSearchMatch(item.Entry, item.Score))
                    .ToList();
                var relatedMatches = rankedSearch.Related
                    .Select(item => ToSearchMatch(item.Entry, item.Score))
                    .ToList();
                var nextStepMatches = rankedSearch.NextSteps
                    .Select(item => ToSearchMatch(item.Entry, item.Score))
                    .ToList();

                rankedMetrics = rankedSearch.Metrics;
                result = new McpGatewaySearchResult(matches, diagnostics, rankedSearch.RankingMode)
                {
                    RelatedMatches = relatedMatches,
                    NextStepMatches = nextStepMatches,
                    FocusedGraphNodeCount = rankedSearch.FocusedGraphNodeCount,
                    FocusedGraphEdgeCount = rankedSearch.FocusedGraphEdgeCount
                };
            }

            _searchRuntimeCache.SetSearchResult(
                snapshot.Version,
                _searchStrategy,
                originalQuery,
                contextSummary,
                flattenedContext,
                limit,
                result,
                rankedMetrics,
                queryNormalized);
        }

        McpGatewayTelemetry.RecordSearch(
            activity,
            _searchStrategy,
            result,
            rankedMetrics,
            cacheHit,
            queryNormalized,
            durationMilliseconds: stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private static McpGatewaySearchMatch ToSearchMatch(ToolCatalogEntry entry, double score)
        => new(
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.SourceKind,
            entry.Descriptor.ToolName,
            entry.Descriptor.DisplayName,
            entry.Descriptor.Description,
            entry.Descriptor.RequiredArguments,
            entry.Descriptor.InputSchemaJson,
            Math.Clamp(score, 0d, 1d));
}
