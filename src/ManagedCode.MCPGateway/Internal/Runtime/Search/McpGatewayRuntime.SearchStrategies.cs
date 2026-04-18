using System.Diagnostics;
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
            addUnavailableDiagnostic: true,
            addFailureDiagnostics: true,
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
            addUnavailableDiagnostic: true,
            addFailureDiagnostics: true,
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
        var vectorRanked = await TryRankWithVectorAsync(
            snapshot,
            searchInput,
            diagnostics,
            applyLexicalBoosts: false,
            addFailureDiagnostics: true,
            cancellationToken);
        if (vectorRanked is null)
        {
            var graphFallback = await RankWithGraphOrEmptyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                addGraphFallbackDiagnostic: true,
                addUnavailableDiagnostic: true,
                addFailureDiagnostics: true,
                cancellationToken);
            AddLowConfidenceGraphDiagnostic(graphFallback, diagnostics);
            return graphFallback;
        }

        if (snapshot.GraphIndex?.CanSearch == true && IsUnusableVectorResult(vectorRanked))
        {
            var graphFallback = await RankWithGraphOrEmptyAsync(
                snapshot,
                searchInput,
                limit,
                diagnostics,
                addGraphFallbackDiagnostic: true,
                addUnavailableDiagnostic: true,
                addFailureDiagnostics: true,
                cancellationToken);
            AddLowConfidenceGraphDiagnostic(graphFallback, diagnostics);
            return graphFallback;
        }

        var graphRanked = await TryRankWithGraphAsync(
            snapshot,
            searchInput,
            limit,
            diagnostics,
            addUnavailableDiagnostic: false,
            addFailureDiagnostics: false,
            cancellationToken);
        if (graphRanked is null)
        {
            return vectorRanked;
        }

        var merged = MergeAutoResults(vectorRanked, graphRanked, limit);
        if (string.Equals(merged.RankingMode, SearchModeHybrid, StringComparison.Ordinal))
        {
            diagnostics.Add(new McpGatewayDiagnostic(HybridVectorMergeDiagnosticCode, HybridVectorMergeMessage));
        }

        return merged;
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
            if (embeddingGeneratorLease.Generator is not IEmbeddingGenerator<string, Embedding<float>> generator)
            {
                return null;
            }

            var embeddingGeneratorFingerprint = GetOrCreateEmbeddingGeneratorFingerprint(generator);
            float[] queryVector;
            double queryMagnitude;
            var cachedQueryEmbedding = await _searchRuntimeCache.TryGetQueryEmbeddingAsync(
                searchInput.VectorQuery,
                embeddingGeneratorFingerprint,
                cancellationToken);
            if (cachedQueryEmbedding.found && cachedQueryEmbedding.embedding is not null)
            {
                queryVector = cachedQueryEmbedding.embedding.Vector;
                queryMagnitude = cachedQueryEmbedding.embedding.Magnitude;
            }
            else
            {
                var embedding = await generator.GenerateAsync(searchInput.VectorQuery, cancellationToken: cancellationToken);
                queryVector = embedding.Vector.ToArray();
                queryMagnitude = CalculateMagnitude(queryVector);
                await _searchRuntimeCache.SetQueryEmbeddingAsync(
                    searchInput.VectorQuery,
                    embeddingGeneratorFingerprint,
                    new McpGatewayQueryEmbedding(queryVector, queryMagnitude),
                    cancellationToken);
            }

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
            return new RankedSearch(ranked, SearchModeVector)
            {
                Metrics = new RankedSearchMetrics(
                    UsedVectorSearch: true,
                    UsedGraphSearch: false,
                    VectorDurationMilliseconds: stopwatch.Elapsed.TotalMilliseconds)
            };
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

    private static bool IsUnusableVectorResult(RankedSearch rankedSearch)
        => rankedSearch.Ranked.Count == 0 || rankedSearch.Ranked[0].Score <= double.Epsilon;

    private static RankedSearch MergeAutoResults(
        RankedSearch vectorRanked,
        RankedSearch graphRanked,
        int limit)
    {
        var primaryToolIds = vectorRanked.Ranked
            .Take(limit)
            .Select(static item => item.Entry.Descriptor.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateToolIds = vectorRanked.Ranked
            .Take(CalculateAutoSupplementCandidateWindow(limit, vectorRanked.Ranked.Count))
            .Select(static item => item.Entry.Descriptor.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var toolId in primaryToolIds)
        {
            candidateToolIds.Add(toolId);
        }

        var related = CollectSupplementMatches(
            [.. graphRanked.Ranked, .. graphRanked.Related],
            candidateToolIds,
            primaryToolIds);
        var nextStepExcludedToolIds = new HashSet<string>(primaryToolIds, StringComparer.OrdinalIgnoreCase);
        nextStepExcludedToolIds.UnionWith(related.Select(static item => item.Entry.Descriptor.ToolId));
        var nextSteps = CollectSupplementMatches(
            graphRanked.NextSteps,
            candidateToolIds,
            nextStepExcludedToolIds);
        var metrics = CombineRankedSearchMetrics(vectorRanked.Metrics, graphRanked.Metrics);

        if (related.Count == 0 && nextSteps.Count == 0)
        {
            return vectorRanked with
            {
                FocusedGraphNodeCount = graphRanked.FocusedGraphNodeCount,
                FocusedGraphEdgeCount = graphRanked.FocusedGraphEdgeCount,
                Metrics = metrics
            };
        }

        return new RankedSearch(vectorRanked.Ranked, SearchModeHybrid)
        {
            Related = related,
            NextSteps = nextSteps,
            FocusedGraphNodeCount = graphRanked.FocusedGraphNodeCount,
            FocusedGraphEdgeCount = graphRanked.FocusedGraphEdgeCount,
            Metrics = metrics
        };
    }

    private static int CalculateAutoSupplementCandidateWindow(int limit, int rankedCount)
        => Math.Min(
            rankedCount,
            Math.Max(limit * AutoSupplementCandidateMultiplier, AutoSupplementMinimumCandidateWindow));

    private static IReadOnlyList<ScoredToolEntry> CollectSupplementMatches(
        IEnumerable<ScoredToolEntry> graphMatches,
        IReadOnlySet<string> candidateToolIds,
        IReadOnlySet<string> excludedToolIds)
    {
        var matches = new List<ScoredToolEntry>();
        var seenToolIds = new HashSet<string>(excludedToolIds, StringComparer.OrdinalIgnoreCase);

        foreach (var match in graphMatches)
        {
            var toolId = match.Entry.Descriptor.ToolId;
            if (!candidateToolIds.Contains(toolId) || !seenToolIds.Add(toolId))
            {
                continue;
            }

            matches.Add(match);
        }

        return matches;
    }

    private static RankedSearchMetrics CombineRankedSearchMetrics(
        RankedSearchMetrics? primaryMetrics,
        RankedSearchMetrics? supplementalMetrics)
        => new(
            UsedVectorSearch: (primaryMetrics?.UsedVectorSearch ?? false) || (supplementalMetrics?.UsedVectorSearch ?? false),
            UsedGraphSearch: (primaryMetrics?.UsedGraphSearch ?? false) || (supplementalMetrics?.UsedGraphSearch ?? false),
            VectorDurationMilliseconds: primaryMetrics?.VectorDurationMilliseconds ?? supplementalMetrics?.VectorDurationMilliseconds,
            GraphDurationMilliseconds: supplementalMetrics?.GraphDurationMilliseconds ?? primaryMetrics?.GraphDurationMilliseconds);
}
