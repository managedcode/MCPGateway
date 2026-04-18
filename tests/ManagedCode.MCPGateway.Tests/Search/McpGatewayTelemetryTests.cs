using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAndBuild_EmitGatewayTelemetryForAutoHybridSearch()
    {
        var activities = new List<Activity>();
        var measurements = new List<TelemetryMeasurement>();
        using var activityListener = CreateGatewayActivityListener(activities);
        using var meterListener = CreateGatewayMeterListener(measurements);
        var embeddingGenerator = new TestEmbeddingGenerator(
            new TestEmbeddingGeneratorOptions
            {
                Metadata = new EmbeddingGeneratorMetadata(
                    "ManagedCode.MCPGateway.Tests",
                    new Uri("https://example.test"),
                    "auto-story-telemetry",
                    3
                ),
                CreateVector = static value =>
                {
                    var normalized = value.ToLowerInvariant();
                    return
                    [
                        ScoreSemanticTerms(
                            normalized,
                            "search story feed items",
                            "story feed",
                            "query text",
                            "search",
                            "search",
                            "query",
                            "items"
                        ),
                        ScoreSemanticTerms(
                            normalized,
                            "detail",
                            "comments",
                            "comment",
                            "detail view"
                        ),
                        ScoreSemanticTerms(normalized, "people", "profile", "person"),
                    ];
                },
            }
        );

        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            options =>
            {
                options.SearchStrategy = McpGatewaySearchStrategy.Auto;
                options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
            },
            embeddingGenerator
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        using var parentActivity = new Activity(
            "ManagedCode.MCPGateway.Tests.TelemetryScope"
        ).Start();
        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            "search story feed items before detail lookup or comments",
            maxResults: 1
        );

        await Assert.That(buildResult.ToolCount).IsEqualTo(4);
        await Assert.That(searchResult.RankingMode).IsEqualTo("hybrid");

        var buildActivity = activities.Last(activity =>
            activity.TraceId == parentActivity.TraceId
            && activity.OperationName == "ManagedCode.MCPGateway.BuildIndex"
            && Equals(activity.GetTagItem("mcpgateway.index.tool_count"), 4)
            && Equals(activity.GetTagItem("mcpgateway.index.graph_enabled"), true)
        );
        var searchActivity = activities.Last(activity =>
            activity.TraceId == parentActivity.TraceId
            && activity.OperationName == "ManagedCode.MCPGateway.Search"
            && string.Equals(
                activity.GetTagItem("mcpgateway.search.ranking_mode")?.ToString(),
                "hybrid",
                StringComparison.Ordinal
            )
            && Equals(activity.GetTagItem("mcpgateway.search.result_count"), 1)
        );

        await Assert.That(buildActivity.GetTagItem("mcpgateway.index.tool_count")).IsEqualTo(4);
        await Assert
            .That((bool?)buildActivity.GetTagItem("mcpgateway.index.graph_enabled") == true)
            .IsTrue();
        await Assert
            .That(
                Convert.ToInt64(
                    buildActivity.GetTagItem("mcpgateway.index.vector_tokens"),
                    CultureInfo.InvariantCulture
                ) > 0
            )
            .IsTrue();
        await Assert
            .That(searchActivity.GetTagItem("mcpgateway.search.configured_strategy")?.ToString())
            .IsEqualTo("Auto");
        await Assert
            .That(searchActivity.GetTagItem("mcpgateway.search.ranking_mode")?.ToString())
            .IsEqualTo("hybrid");
        await Assert
            .That((bool?)searchActivity.GetTagItem("mcpgateway.search.used_vector") == true)
            .IsTrue();
        await Assert
            .That((bool?)searchActivity.GetTagItem("mcpgateway.search.used_graph") == true)
            .IsTrue();
        await Assert
            .That((bool?)searchActivity.GetTagItem("mcpgateway.search.cache_hit") == false)
            .IsTrue();
        await Assert.That(searchActivity.GetTagItem("mcpgateway.search.result_count")).IsEqualTo(1);
        await Assert
            .That(
                Convert.ToInt64(
                    searchActivity.GetTagItem("mcpgateway.search.vector_tokens"),
                    CultureInfo.InvariantCulture
                ) > 0
            )
            .IsTrue();
        await Assert
            .That(searchResult.RelatedMatches.Count + searchResult.NextStepMatches.Count > 0)
            .IsTrue();

        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.index.builds"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.index.build.duration"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.index.build.vector.tokens"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.requests"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.duration"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.vector.duration"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.vector.tokens"
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.graph.duration"
                )
            )
            .IsTrue();

        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.index.build.vector.tokens"
                    && measurement.Value > 0
                )
            )
            .IsTrue();

        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.requests"
                    && string.Equals(
                        measurement.Tags["mcpgateway.search.ranking_mode"]?.ToString(),
                        "hybrid",
                        StringComparison.Ordinal
                    )
                    && Convert.ToInt32(
                        measurement.Tags["mcpgateway.search.result_count"],
                        CultureInfo.InvariantCulture
                    ) == 1
                    && (bool?)measurement.Tags["mcpgateway.search.used_vector"] == true
                    && (bool?)measurement.Tags["mcpgateway.search.used_graph"] == true
                    && (bool?)measurement.Tags["mcpgateway.search.cache_hit"] == false
                )
            )
            .IsTrue();

        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.vector.tokens"
                    && measurement.Value > 0
                )
            )
            .IsTrue();
    }

    private static ActivityListener CreateGatewayActivityListener(ICollection<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "ManagedCode.MCPGateway",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener CreateGatewayMeterListener(
        ICollection<TelemetryMeasurement> measurements
    )
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "ManagedCode.MCPGateway")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                measurements.Add(
                    new TelemetryMeasurement(
                        instrument.Name,
                        measurement,
                        CreateTagDictionary(tags)
                    )
                )
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
                measurements.Add(
                    new TelemetryMeasurement(
                        instrument.Name,
                        measurement,
                        CreateTagDictionary(tags)
                    )
                )
        );
        listener.Start();
        return listener;
    }

    private static IReadOnlyDictionary<string, object?> CreateTagDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }

    private sealed record TelemetryMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags
    );
}
