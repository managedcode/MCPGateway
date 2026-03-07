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

        var rawQuery = request.Query?.Trim();
        var effectiveQuery = BuildEffectiveSearchQuery(request);
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            var browse = snapshot.Entries
                .Take(limit)
                .Select(static entry => ToSearchMatch(entry, 0d))
                .ToList();
            return new McpGatewaySearchResult(browse, diagnostics, SearchModeBrowse);
        }

        IReadOnlyList<ScoredToolEntry> ranked;
        var rankingMode = SearchModeLexical;
        var shouldPreferVectorSearch = _searchStrategy is not McpGatewaySearchStrategy.Tokenizer && snapshot.HasVectors;
        if (shouldPreferVectorSearch)
        {
            try
            {
                await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
                if (embeddingGeneratorLease.Generator is IEmbeddingGenerator<string, Embedding<float>> generator)
                {
                    var embedding = await generator.GenerateAsync(effectiveQuery, cancellationToken: cancellationToken);
                    var queryVector = embedding.Vector.ToArray();
                    var queryMagnitude = CalculateMagnitude(queryVector);
                    if (queryMagnitude > double.Epsilon)
                    {
                        ranked = snapshot.Entries
                            .Select(entry => new ScoredToolEntry(
                                entry,
                                ApplySearchBoosts(
                                    entry,
                                    rawQuery ?? effectiveQuery,
                                    CalculateCosine(entry, queryVector, queryMagnitude))))
                            .OrderByDescending(static item => item.Score)
                            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        rankingMode = SearchModeVector;
                    }
                    else
                    {
                        ranked = RankLexically(snapshot, request, effectiveQuery);
                        diagnostics.Add(new McpGatewayDiagnostic(QueryVectorEmptyDiagnosticCode, QueryVectorEmptyMessage));
                    }
                }
                else
                {
                    ranked = RankLexically(snapshot, request, effectiveQuery);
                    diagnostics.Add(new McpGatewayDiagnostic(
                        LexicalFallbackDiagnosticCode,
                        LexicalFallbackMessage));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ranked = RankLexically(snapshot, request, effectiveQuery);
                diagnostics.Add(new McpGatewayDiagnostic(
                    VectorSearchFailedDiagnosticCode,
                    string.Format(CultureInfo.InvariantCulture, VectorSearchFailedMessageFormat, ex.GetBaseException().Message)));
                _logger.LogWarning(ex, GatewayVectorSearchFailedLogMessage);
            }
        }
        else
        {
            ranked = RankLexically(snapshot, request, effectiveQuery);
            if (_searchStrategy is not McpGatewaySearchStrategy.Tokenizer)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    LexicalFallbackDiagnosticCode,
                    LexicalFallbackMessage));
            }
        }

        var matches = ranked
            .Take(limit)
            .Select(item => ToSearchMatch(item.Entry, item.Score))
            .ToList();

        return new McpGatewaySearchResult(matches, diagnostics, rankingMode);
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
