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

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var limit = Math.Clamp(request.MaxResults.GetValueOrDefault(_defaultSearchLimit), 1, _maxSearchResults);
        var diagnostics = new List<McpGatewayDiagnostic>();

        if (snapshot.Entries.Count == 0)
        {
            return new McpGatewaySearchResult([], diagnostics, SearchModeEmpty);
        }

        var normalizedQuery = await NormalizeSearchQueryAsync(request.Query, diagnostics, cancellationToken);
        var searchInput = BuildSearchInput(request, normalizedQuery);
        if (string.IsNullOrWhiteSpace(searchInput.EffectiveQuery))
        {
            var browse = snapshot.Entries
                .Take(limit)
                .Select(static entry => ToSearchMatch(entry, 0d))
                .ToList();
            return new McpGatewaySearchResult(browse, diagnostics, SearchModeBrowse);
        }

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

        return new McpGatewaySearchResult(matches, diagnostics, rankedSearch.RankingMode)
        {
            RelatedMatches = relatedMatches,
            NextStepMatches = nextStepMatches,
            FocusedGraphNodeCount = rankedSearch.FocusedGraphNodeCount,
            FocusedGraphEdgeCount = rankedSearch.FocusedGraphEdgeCount
        };
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
