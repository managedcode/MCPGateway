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
    private const string SearchGraphDurationInstrumentName = "mcpgateway.search.graph.duration";
    private const string IndexBuildsInstrumentName = "mcpgateway.index.builds";
    private const string IndexBuildDurationInstrumentName = "mcpgateway.index.build.duration";
    private const string ConfiguredStrategyTagName = "mcpgateway.search.configured_strategy";
    private const string RankingModeTagName = "mcpgateway.search.ranking_mode";
    private const string UsedVectorTagName = "mcpgateway.search.used_vector";
    private const string UsedGraphTagName = "mcpgateway.search.used_graph";
    private const string CacheHitTagName = "mcpgateway.search.cache_hit";
    private const string QueryNormalizedTagName = "mcpgateway.search.query_normalized";
    private const string ResultCountTagName = "mcpgateway.search.result_count";
    private const string RelatedCountTagName = "mcpgateway.search.related_count";
    private const string NextStepCountTagName = "mcpgateway.search.next_step_count";
    private const string FocusedGraphNodeCountTagName = "mcpgateway.search.focused_graph_node_count";
    private const string FocusedGraphEdgeCountTagName = "mcpgateway.search.focused_graph_edge_count";
    private const string ToolCountTagName = "mcpgateway.index.tool_count";
    private const string VectorizedToolCountTagName = "mcpgateway.index.vectorized_tool_count";
    private const string GraphEnabledTagName = "mcpgateway.index.graph_enabled";
    private const string GraphNodeCountTagName = "mcpgateway.index.graph_node_count";
    private const string GraphEdgeCountTagName = "mcpgateway.index.graph_edge_count";

    private static readonly ActivitySource ActivitySource = new(DiagnosticsName);
    private static readonly Meter Meter = new(DiagnosticsName);
    private static readonly Counter<long> SearchRequests = Meter.CreateCounter<long>(
        SearchRequestsInstrumentName,
        unit: "{request}",
        description: "Number of gateway search requests.");
    private static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        SearchDurationInstrumentName,
        unit: "ms",
        description: "Gateway search duration in milliseconds.");
    private static readonly Histogram<double> SearchVectorDuration = Meter.CreateHistogram<double>(
        SearchVectorDurationInstrumentName,
        unit: "ms",
        description: "Vector ranking duration in milliseconds.");
    private static readonly Histogram<double> SearchGraphDuration = Meter.CreateHistogram<double>(
        SearchGraphDurationInstrumentName,
        unit: "ms",
        description: "Graph ranking duration in milliseconds.");
    private static readonly Counter<long> IndexBuilds = Meter.CreateCounter<long>(
        IndexBuildsInstrumentName,
        unit: "{build}",
        description: "Number of gateway index builds.");
    private static readonly Histogram<double> IndexBuildDuration = Meter.CreateHistogram<double>(
        IndexBuildDurationInstrumentName,
        unit: "ms",
        description: "Gateway index build duration in milliseconds.");

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
        double durationMilliseconds)
    {
        var tags = CreateSearchTags(configuredStrategy, result, rankedMetrics, cacheHit, queryNormalized);

        SearchRequests.Add(1, tags);
        SearchDuration.Record(durationMilliseconds, tags);

        if (!cacheHit && rankedMetrics?.VectorDurationMilliseconds is double vectorDurationMilliseconds)
        {
            SearchVectorDuration.Record(vectorDurationMilliseconds, tags);
        }

        if (!cacheHit && rankedMetrics?.GraphDurationMilliseconds is double graphDurationMilliseconds)
        {
            SearchGraphDuration.Record(graphDurationMilliseconds, tags);
        }

        if (activity is null)
        {
            return;
        }

        ApplyTags(activity, tags);
        if (!cacheHit && rankedMetrics?.VectorDurationMilliseconds is double vectorDuration)
        {
            activity.SetTag("mcpgateway.search.vector_duration_ms", vectorDuration);
        }

        if (!cacheHit && rankedMetrics?.GraphDurationMilliseconds is double graphDuration)
        {
            activity.SetTag("mcpgateway.search.graph_duration_ms", graphDuration);
        }

        activity.SetTag("mcpgateway.search.duration_ms", durationMilliseconds);
    }

    public static void RecordIndexBuild(
        Activity? activity,
        McpGatewaySearchStrategy configuredStrategy,
        McpGatewayIndexBuildResult result,
        double durationMilliseconds)
    {
        var tags = new TagList
        {
            { ConfiguredStrategyTagName, configuredStrategy.ToString() },
            { ToolCountTagName, result.ToolCount },
            { VectorizedToolCountTagName, result.VectorizedToolCount },
            { GraphEnabledTagName, result.IsGraphSearchEnabled },
            { GraphNodeCountTagName, result.GraphNodeCount },
            { GraphEdgeCountTagName, result.GraphEdgeCount }
        };

        IndexBuilds.Add(1, tags);
        IndexBuildDuration.Record(durationMilliseconds, tags);

        if (activity is null)
        {
            return;
        }

        ApplyTags(activity, tags);
        activity.SetTag("mcpgateway.index.build_duration_ms", durationMilliseconds);
    }

    private static TagList CreateSearchTags(
        McpGatewaySearchStrategy configuredStrategy,
        McpGatewaySearchResult result,
        RankedSearchMetrics? rankedMetrics,
        bool cacheHit,
        bool queryNormalized)
    {
        return new TagList
        {
            { ConfiguredStrategyTagName, configuredStrategy.ToString() },
            { RankingModeTagName, result.RankingMode },
            { UsedVectorTagName, rankedMetrics?.UsedVectorSearch ?? false },
            { UsedGraphTagName, rankedMetrics?.UsedGraphSearch ?? false },
            { CacheHitTagName, cacheHit },
            { QueryNormalizedTagName, queryNormalized },
            { ResultCountTagName, result.Matches.Count },
            { RelatedCountTagName, result.RelatedMatches.Count },
            { NextStepCountTagName, result.NextStepMatches.Count },
            { FocusedGraphNodeCountTagName, result.FocusedGraphNodeCount },
            { FocusedGraphEdgeCountTagName, result.FocusedGraphEdgeCount }
        };
    }

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
    double? GraphDurationMilliseconds = null);
