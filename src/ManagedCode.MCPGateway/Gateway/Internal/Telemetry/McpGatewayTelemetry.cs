using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayTelemetry
{
    private const string DiagnosticsName = "ManagedCode.MCPGateway";
    private const string SearchActivityName = "ManagedCode.MCPGateway.Search";
    private const string BuildIndexActivityName = "ManagedCode.MCPGateway.BuildIndex";
    private const string SearchRequestsInstrumentName = "mcpgateway.search.requests";
    private const string SearchDurationInstrumentName = "mcpgateway.search.duration";
    private const string SearchVectorDurationInstrumentName = "mcpgateway.search.vector.duration";
    private const string SearchVectorTokensInstrumentName = "mcpgateway.search.vector.tokens";
    private const string SearchGraphDurationInstrumentName = "mcpgateway.search.graph.duration";
    private const string IndexBuildsInstrumentName = "mcpgateway.index.builds";
    private const string IndexBuildDurationInstrumentName = "mcpgateway.index.build.duration";
    private const string IndexBuildVectorTokensInstrumentName =
        "mcpgateway.index.build.vector.tokens";
    private const string ConfiguredStrategyTagName = "mcpgateway.search.configured_strategy";
    private const string RankingModeTagName = "mcpgateway.search.ranking_mode";
    private const string UsedVectorTagName = "mcpgateway.search.used_vector";
    private const string UsedGraphTagName = "mcpgateway.search.used_graph";
    private const string CacheHitTagName = "mcpgateway.search.cache_hit";
    private const string QueryNormalizedTagName = "mcpgateway.search.query_normalized";
    private const string ResultCountTagName = "mcpgateway.search.result_count";
    private const string RelatedCountTagName = "mcpgateway.search.related_count";
    private const string NextStepCountTagName = "mcpgateway.search.next_step_count";
    private const string FocusedGraphNodeCountTagName =
        "mcpgateway.search.focused_graph_node_count";
    private const string FocusedGraphEdgeCountTagName =
        "mcpgateway.search.focused_graph_edge_count";
    private const string ToolCountTagName = "mcpgateway.index.tool_count";
    private const string VectorizedToolCountTagName = "mcpgateway.index.vectorized_tool_count";
    private const string GraphEnabledTagName = "mcpgateway.index.graph_enabled";
    private const string GraphNodeCountTagName = "mcpgateway.index.graph_node_count";
    private const string GraphEdgeCountTagName = "mcpgateway.index.graph_edge_count";
    private const string SearchVectorTokensTagName = "mcpgateway.search.vector_tokens";
    private const string SearchVectorDurationMillisecondsTagName =
        "mcpgateway.search.vector_duration_ms";
    private const string SearchGraphDurationMillisecondsTagName =
        "mcpgateway.search.graph_duration_ms";
    private const string SearchDurationMillisecondsTagName = "mcpgateway.search.duration_ms";
    private const string IndexBuildDurationMillisecondsTagName =
        "mcpgateway.index.build_duration_ms";
    private const string IndexVectorTokensTagName = "mcpgateway.index.vector_tokens";

    private static readonly ActivitySource ActivitySource = new(DiagnosticsName);
    private static readonly Meter Meter = new(DiagnosticsName);
    private static readonly Counter<long> SearchRequests = Meter.CreateCounter<long>(
        SearchRequestsInstrumentName,
        unit: "{request}",
        description: "Number of gateway search requests."
    );
    private static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        SearchDurationInstrumentName,
        unit: "ms",
        description: "Gateway search duration in milliseconds."
    );
    private static readonly Histogram<double> SearchVectorDuration = Meter.CreateHistogram<double>(
        SearchVectorDurationInstrumentName,
        unit: "ms",
        description: "Vector ranking duration in milliseconds."
    );
    private static readonly Histogram<long> SearchVectorTokens = Meter.CreateHistogram<long>(
        SearchVectorTokensInstrumentName,
        unit: "{token}",
        description: "Tokens consumed by vector embedding generation during search."
    );
    private static readonly Histogram<double> SearchGraphDuration = Meter.CreateHistogram<double>(
        SearchGraphDurationInstrumentName,
        unit: "ms",
        description: "Graph ranking duration in milliseconds."
    );
    private static readonly Counter<long> IndexBuilds = Meter.CreateCounter<long>(
        IndexBuildsInstrumentName,
        unit: "{build}",
        description: "Number of gateway index builds."
    );
    private static readonly Histogram<double> IndexBuildDuration = Meter.CreateHistogram<double>(
        IndexBuildDurationInstrumentName,
        unit: "ms",
        description: "Gateway index build duration in milliseconds."
    );
    private static readonly Histogram<long> IndexBuildVectorTokens = Meter.CreateHistogram<long>(
        IndexBuildVectorTokensInstrumentName,
        unit: "{token}",
        description: "Tokens consumed by vector embedding generation during index builds."
    );

    private static readonly Histogram<long> SearchResultCount = Meter.CreateHistogram<long>(ResultCountTagName);
    private static readonly Histogram<long> SearchRelatedCount = Meter.CreateHistogram<long>(RelatedCountTagName);
    private static readonly Histogram<long> SearchNextStepCount = Meter.CreateHistogram<long>(NextStepCountTagName);
    private static readonly Histogram<long> SearchFocusedNodes = Meter.CreateHistogram<long>(FocusedGraphNodeCountTagName);
    private static readonly Histogram<long> SearchFocusedEdges = Meter.CreateHistogram<long>(FocusedGraphEdgeCountTagName);
    private static readonly Histogram<long> IndexTools = Meter.CreateHistogram<long>(ToolCountTagName);
    private static readonly Histogram<long> IndexVectorizedTools = Meter.CreateHistogram<long>(VectorizedToolCountTagName);
    private static readonly Histogram<long> IndexNodes = Meter.CreateHistogram<long>(GraphNodeCountTagName);
    private static readonly Histogram<long> IndexEdges = Meter.CreateHistogram<long>(GraphEdgeCountTagName);

    public static Activity? StartSearchActivity(McpGatewaySearchStrategy configuredStrategy)
    {
        var activity = ActivitySource.StartActivity(SearchActivityName, ActivityKind.Internal);
        activity?.SetTag(ConfiguredStrategyTagName, configuredStrategy.ToString());
        return activity;
    }

    public static Activity? StartBuildIndexActivity(McpGatewaySearchStrategy configuredStrategy)
    {
        var activity = ActivitySource.StartActivity(BuildIndexActivityName, ActivityKind.Internal);
        activity?.SetTag(ConfiguredStrategyTagName, configuredStrategy.ToString());
        return activity;
    }

    public static void RecordSearch(
        Activity? activity,
        McpGatewaySearchStrategy configuredStrategy,
        McpGatewaySearchResult result,
        RankedSearchMetrics? rankedMetrics,
        bool cacheHit,
        bool queryNormalized,
        double durationMilliseconds
    )
    {
        var tags = CreateSearchTags(
            configuredStrategy,
            result,
            rankedMetrics,
            cacheHit,
            queryNormalized
        );

        SearchRequests.Add(1, tags);
        SearchResultCount.Record(result.Matches.Count, tags);
        SearchRelatedCount.Record(result.RelatedMatches.Count, tags);
        SearchNextStepCount.Record(result.NextStepMatches.Count, tags);
        SearchFocusedNodes.Record(result.FocusedGraphNodeCount, tags);
        SearchFocusedEdges.Record(result.FocusedGraphEdgeCount, tags);
        SearchDuration.Record(durationMilliseconds, tags);

        if (
            !cacheHit
            && rankedMetrics?.VectorDurationMilliseconds is double vectorDurationMilliseconds
        )
        {
            SearchVectorDuration.Record(vectorDurationMilliseconds, tags);
        }

        if (!cacheHit && rankedMetrics?.UsedVectorSearch == true)
        {
            SearchVectorTokens.Record(rankedMetrics.VectorTotalTokenCount, tags);
        }

        if (
            !cacheHit
            && rankedMetrics?.GraphDurationMilliseconds is double graphDurationMilliseconds
        )
        {
            SearchGraphDuration.Record(graphDurationMilliseconds, tags);
        }

        if (activity is null)
        {
            return;
        }

        ApplyTags(activity, tags);
        activity.SetTag(ResultCountTagName, result.Matches.Count);
        activity.SetTag(RelatedCountTagName, result.RelatedMatches.Count);
        activity.SetTag(NextStepCountTagName, result.NextStepMatches.Count);
        activity.SetTag(FocusedGraphNodeCountTagName, result.FocusedGraphNodeCount);
        activity.SetTag(FocusedGraphEdgeCountTagName, result.FocusedGraphEdgeCount);
        if (!cacheHit && rankedMetrics?.VectorDurationMilliseconds is double vectorDuration)
        {
            activity.SetTag(SearchVectorDurationMillisecondsTagName, vectorDuration);
        }

        if (!cacheHit && rankedMetrics?.UsedVectorSearch == true)
        {
            activity.SetTag(SearchVectorTokensTagName, rankedMetrics.VectorTotalTokenCount);
        }

        if (!cacheHit && rankedMetrics?.GraphDurationMilliseconds is double graphDuration)
        {
            activity.SetTag(SearchGraphDurationMillisecondsTagName, graphDuration);
        }

        activity.SetTag(SearchDurationMillisecondsTagName, durationMilliseconds);
    }

    public static void RecordIndexBuild(
        Activity? activity,
        McpGatewaySearchStrategy configuredStrategy,
        McpGatewayIndexBuildResult result,
        VectorTokenUsage vectorTokenUsage,
        double durationMilliseconds
    )
    {
        var tags = new TagList
        {
            { ConfiguredStrategyTagName, configuredStrategy.ToString() },
            { GraphEnabledTagName, result.IsGraphSearchEnabled },
        };

        IndexBuilds.Add(1, tags);
        IndexTools.Record(result.ToolCount, tags);
        IndexVectorizedTools.Record(result.VectorizedToolCount, tags);
        IndexNodes.Record(result.GraphNodeCount, tags);
        IndexEdges.Record(result.GraphEdgeCount, tags);
        IndexBuildDuration.Record(durationMilliseconds, tags);
        if (result.IsVectorSearchEnabled || vectorTokenUsage.TotalTokenCount > 0)
        {
            IndexBuildVectorTokens.Record(vectorTokenUsage.TotalTokenCount, tags);
        }

        if (activity is null)
        {
            return;
        }

        ApplyTags(activity, tags);
        activity.SetTag(ToolCountTagName, result.ToolCount);
        activity.SetTag(VectorizedToolCountTagName, result.VectorizedToolCount);
        activity.SetTag(GraphNodeCountTagName, result.GraphNodeCount);
        activity.SetTag(GraphEdgeCountTagName, result.GraphEdgeCount);
        if (result.IsVectorSearchEnabled || vectorTokenUsage.TotalTokenCount > 0)
        {
            activity.SetTag(IndexVectorTokensTagName, vectorTokenUsage.TotalTokenCount);
        }

        activity.SetTag(IndexBuildDurationMillisecondsTagName, durationMilliseconds);
    }

    private static TagList CreateSearchTags(
        McpGatewaySearchStrategy configuredStrategy,
        McpGatewaySearchResult result,
        RankedSearchMetrics? rankedMetrics,
        bool cacheHit,
        bool queryNormalized
    )
    {
        var usedVectorSearch =
            rankedMetrics?.UsedVectorSearch ?? UsesVectorSearch(result.RankingMode);
        var usedGraphSearch = rankedMetrics?.UsedGraphSearch ?? UsesGraphSearch(result.RankingMode);

        return new TagList
        {
            { ConfiguredStrategyTagName, configuredStrategy.ToString() },
            { RankingModeTagName, result.RankingMode },
            { UsedVectorTagName, usedVectorSearch },
            { UsedGraphTagName, usedGraphSearch },
            { CacheHitTagName, cacheHit },
            { QueryNormalizedTagName, queryNormalized },
        };
    }

    private static bool UsesVectorSearch(string rankingMode) =>
        string.Equals(rankingMode, "vector", StringComparison.Ordinal)
        || string.Equals(rankingMode, "hybrid", StringComparison.Ordinal);

    private static bool UsesGraphSearch(string rankingMode) =>
        string.Equals(rankingMode, "graph", StringComparison.Ordinal)
        || string.Equals(rankingMode, "hybrid", StringComparison.Ordinal);

    private static void ApplyTags(Activity activity, TagList tags)
    {
        foreach (var tag in tags)
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }
}

internal sealed record RankedSearchMetrics(
    bool UsedVectorSearch,
    bool UsedGraphSearch,
    double? VectorDurationMilliseconds = null,
    double? GraphDurationMilliseconds = null,
    long VectorInputTokenCount = 0,
    long VectorTotalTokenCount = 0
);
