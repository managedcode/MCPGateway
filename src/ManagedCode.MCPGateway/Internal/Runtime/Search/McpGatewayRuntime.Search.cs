using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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

        RankedSearch rankedSearch;
        var shouldPreferVectorSearch =
            (_searchStrategy is McpGatewaySearchStrategy.Embeddings ||
             (_searchStrategy is McpGatewaySearchStrategy.Auto && snapshot.GraphIndex?.CanSearch != true)) &&
            snapshot.HasVectors;
        if (shouldPreferVectorSearch)
        {
            try
            {
                await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
                if (embeddingGeneratorLease.Generator is IEmbeddingGenerator<string, Embedding<float>> generator)
                {
                    var embedding = await generator.GenerateAsync(searchInput.EffectiveQuery, cancellationToken: cancellationToken);
                    var queryVector = embedding.Vector.ToArray();
                    var queryMagnitude = CalculateMagnitude(queryVector);
                    if (queryMagnitude > double.Epsilon)
                    {
                        var ranked = snapshot.Entries
                            .Select(entry => new ScoredToolEntry(
                                entry,
                                ApplySearchBoosts(
                                    entry,
                                    searchInput.BoostQuery,
                                    CalculateCosine(entry, queryVector, queryMagnitude))))
                            .OrderByDescending(static item => item.Score)
                            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        rankedSearch = new RankedSearch(ranked, SearchModeVector);
                    }
                    else
                    {
                        diagnostics.Add(new McpGatewayDiagnostic(QueryVectorEmptyDiagnosticCode, QueryVectorEmptyMessage));
                        rankedSearch = await RankWithGraphOrEmptyAsync(
                            snapshot,
                            searchInput,
                            limit,
                            diagnostics,
                            addGraphFallbackDiagnostic: false,
                            cancellationToken);
                    }
                }
                else
                {
                    rankedSearch = await RankWithGraphOrEmptyAsync(
                        snapshot,
                        searchInput,
                        limit,
                        diagnostics,
                        addGraphFallbackDiagnostic: true,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    VectorSearchFailedDiagnosticCode,
                    string.Format(CultureInfo.InvariantCulture, VectorSearchFailedMessageFormat, ex.GetBaseException().Message)));
                _logger.LogWarning(ex, GatewayVectorSearchFailedLogMessage);
                rankedSearch = await RankWithGraphOrEmptyAsync(
                    snapshot,
                    searchInput,
                    limit,
                    diagnostics,
                    addGraphFallbackDiagnostic: false,
                    cancellationToken);
            }
        }
        else if (_searchStrategy is McpGatewaySearchStrategy.Embeddings)
        {
            rankedSearch = await RankWithGraphOrEmptyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                addGraphFallbackDiagnostic: true,
                cancellationToken);
        }
        else
        {
            rankedSearch = await RankWithGraphOrEmptyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                addGraphFallbackDiagnostic: false,
                cancellationToken);
        }

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
            score);
}
