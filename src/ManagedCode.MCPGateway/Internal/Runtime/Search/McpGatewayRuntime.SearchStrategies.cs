using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<RankedSearch> RankWithConfiguredStrategyAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken)
        => _searchStrategy switch
        {
            McpGatewaySearchStrategy.Embeddings => await RankWithEmbeddingsStrategyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                cancellationToken),
            McpGatewaySearchStrategy.Auto => await RankWithAutoStrategyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                cancellationToken),
            _ => await RankWithGraphStrategyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                cancellationToken)
        };

    private async Task<RankedSearch> RankWithGraphStrategyAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var graphRanked = await RankWithGraphOrEmptyAsync(
            snapshot,
            searchInput,
            limit,
            diagnostics,
            addGraphFallbackDiagnostic: false,
            cancellationToken);
        AddLowConfidenceGraphDiagnostic(graphRanked, diagnostics);
        return graphRanked;
    }

    private async Task<RankedSearch> RankWithEmbeddingsStrategyAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var vectorRanked = await TryRankWithVectorAsync(
            snapshot,
            searchInput,
            diagnostics,
            applyLexicalBoosts: true,
            addFailureDiagnostics: true,
            cancellationToken);
        if (vectorRanked is not null)
        {
            return vectorRanked;
        }

        var graphRanked = await RankWithGraphOrEmptyAsync(
            snapshot,
            searchInput,
            limit,
            diagnostics,
            addGraphFallbackDiagnostic: true,
            cancellationToken);
        AddLowConfidenceGraphDiagnostic(graphRanked, diagnostics);
        return graphRanked;
    }

    private async Task<RankedSearch> RankWithAutoStrategyAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        int limit,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (snapshot.GraphIndex?.CanSearch != true)
        {
            var vectorWithoutGraph = await TryRankWithVectorAsync(
                snapshot,
                searchInput,
                diagnostics,
                applyLexicalBoosts: false,
                addFailureDiagnostics: true,
                cancellationToken);
            if (vectorWithoutGraph is not null)
            {
                return vectorWithoutGraph;
            }

            return await RankWithGraphOrEmptyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                addGraphFallbackDiagnostic: false,
                cancellationToken);
        }

        var graphRanked = await RankWithGraphOrEmptyAsync(
            snapshot,
            searchInput,
            limit,
            diagnostics,
            addGraphFallbackDiagnostic: false,
            cancellationToken);
        AddLowConfidenceGraphDiagnostic(graphRanked, diagnostics);
        if (!IsLowConfidenceGraphResult(graphRanked) || !snapshot.HasVectors)
        {
            return graphRanked;
        }

        var vectorRanked = await TryRankWithVectorAsync(
            snapshot,
            searchInput,
            diagnostics,
            applyLexicalBoosts: false,
            addFailureDiagnostics: true,
            cancellationToken);
        if (vectorRanked is null)
        {
            return graphRanked;
        }

        diagnostics.Add(new McpGatewayDiagnostic(HybridVectorMergeDiagnosticCode, HybridVectorMergeMessage));
        return MergeHybridResults(graphRanked, vectorRanked, limit);
    }

    private async Task<RankedSearch?> TryRankWithVectorAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        IList<McpGatewayDiagnostic> diagnostics,
        bool applyLexicalBoosts,
        bool addFailureDiagnostics,
        CancellationToken cancellationToken)
    {
        if (!snapshot.HasVectors)
        {
            return null;
        }

        try
        {
            await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
            if (embeddingGeneratorLease.Generator is not IEmbeddingGenerator<string, Embedding<float>> generator)
            {
                return null;
            }

            var embedding = await generator.GenerateAsync(searchInput.EffectiveQuery, cancellationToken: cancellationToken);
            var queryVector = embedding.Vector.ToArray();
            var queryMagnitude = CalculateMagnitude(queryVector);
            if (queryMagnitude <= double.Epsilon)
            {
                if (addFailureDiagnostics)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(QueryVectorEmptyDiagnosticCode, QueryVectorEmptyMessage));
                }

                return null;
            }

            var ranked = snapshot.Entries
                .Select(entry => new ScoredToolEntry(
                    entry,
                    ScoreVectorEntry(
                        entry,
                        searchInput.BoostQuery,
                        queryVector,
                        queryMagnitude,
                        applyLexicalBoosts)))
                .OrderByDescending(static item => item.Score)
                .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new RankedSearch(ranked, SearchModeVector);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (addFailureDiagnostics)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    VectorSearchFailedDiagnosticCode,
                    string.Format(CultureInfo.InvariantCulture, VectorSearchFailedMessageFormat, ex.GetBaseException().Message)));
            }

            _logger.LogWarning(ex, GatewayVectorSearchFailedLogMessage);
            return null;
        }
    }

    private static double ScoreVectorEntry(
        ToolCatalogEntry entry,
        string boostQuery,
        float[] queryVector,
        double queryMagnitude,
        bool applyLexicalBoosts)
    {
        var cosine = Math.Max(0d, CalculateCosine(entry, queryVector, queryMagnitude));
        return applyLexicalBoosts
            ? ApplySearchBoosts(entry, boostQuery, cosine)
            : cosine;
    }

    private static RankedSearch MergeHybridResults(
        RankedSearch graphRanked,
        RankedSearch vectorRanked,
        int limit)
    {
        var merged = new Dictionary<string, HybridCandidate>(StringComparer.OrdinalIgnoreCase);

        var graphPrimary = graphRanked.Ranked.Take(limit).ToList();
        for (var index = 0; index < graphPrimary.Count; index++)
        {
            var item = graphPrimary[index];
            merged[item.Entry.Descriptor.ToolId] = new HybridCandidate(
                item.Entry,
                item.Score,
                null,
                index,
                int.MaxValue);
        }

        var vectorPrimary = vectorRanked.Ranked.Take(limit).ToList();
        for (var index = 0; index < vectorPrimary.Count; index++)
        {
            var item = vectorPrimary[index];
            if (merged.TryGetValue(item.Entry.Descriptor.ToolId, out var existing))
            {
                merged[item.Entry.Descriptor.ToolId] = existing with
                {
                    VectorScore = item.Score,
                    VectorRank = index
                };
                continue;
            }

            merged[item.Entry.Descriptor.ToolId] = new HybridCandidate(
                item.Entry,
                null,
                item.Score,
                int.MaxValue,
                index);
        }

        var primary = merged.Values
            .Select(static candidate => new ScoredToolEntry(candidate.Entry, candidate.GetMergedScore()))
            .OrderByDescending(static item => item.Score)
            .ThenBy(item =>
                merged[item.Entry.Descriptor.ToolId].GraphRank,
                Comparer<int>.Default)
            .ThenBy(item =>
                merged[item.Entry.Descriptor.ToolId].VectorRank,
                Comparer<int>.Default)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RankedSearch(primary, SearchModeHybrid)
        {
            FocusedGraphNodeCount = graphRanked.FocusedGraphNodeCount,
            FocusedGraphEdgeCount = graphRanked.FocusedGraphEdgeCount
        };
    }

    private sealed record HybridCandidate(
        ToolCatalogEntry Entry,
        double? GraphScore,
        double? VectorScore,
        int GraphRank,
        int VectorRank)
    {
        public double GetMergedScore()
        {
            if (GraphScore is double graphScore && VectorScore is double vectorScore)
            {
                return Math.Max(graphScore, vectorScore);
            }

            if (GraphScore is double graphOnlyScore)
            {
                return graphOnlyScore;
            }

            return (VectorScore ?? 0d) * HybridVectorOnlyCandidateDamping;
        }
    }
}
