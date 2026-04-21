using System.Diagnostics;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyReusesNormalizedQueryAndQueryEmbeddingAcrossLimitChanges()
    {
        var chatClient = CreateNotificationsRewriteClient();
        var embeddingGenerator = CreateNotificationAutoEmbeddingGenerator();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoNotificationAndFamilyTools,
            embeddingGenerator,
            searchQueryChatClient: chatClient,
            useInMemorySearchCache: true
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();

        var firstSearch = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 1);
        var secondSearch = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 2);

        await Assert
            .That(firstSearch.Matches[0].ToolId)
            .IsEqualTo("local:notification_activity_search");
        await Assert
            .That(secondSearch.Matches[0].ToolId)
            .IsEqualTo("local:notification_activity_search");
        await Assert.That(secondSearch.Matches.Count).IsEqualTo(2);
        await Assert.That(chatClient.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert
            .That(embeddingGenerator.Calls[1].Single())
            .Contains("english query: notification inbox alerts unread activity");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_EmitsCacheHitTelemetryForRepeatedExactRequest()
    {
        var activities = new List<Activity>();
        var measurements = new List<TelemetryMeasurement>();
        using var activityListener = CreateGatewayActivityListener(activities);
        using var meterListener = CreateGatewayMeterListener(measurements);
        var chatClient = CreateNotificationsRewriteClient();
        var embeddingGenerator = CreateNotificationAutoEmbeddingGenerator();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoNotificationAndFamilyTools,
            embeddingGenerator,
            searchQueryChatClient: chatClient,
            useInMemorySearchCache: true
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        _ = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 2);

        measurements.Clear();
        activities.Clear();

        using var parentActivity = new Activity("ManagedCode.MCPGateway.Tests.SearchCache").Start();
        var parentTraceId = parentActivity.TraceId;
        var cachedSearch = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 2);

        await Assert
            .That(cachedSearch.Matches[0].ToolId)
            .IsEqualTo("local:notification_activity_search");
        await Assert.That(chatClient.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);

        var cachedSearchActivity = activities.Single(activity =>
            activity.TraceId == parentTraceId
            && activity.OperationName == "ManagedCode.MCPGateway.Search"
        );
        await Assert
            .That((bool?)cachedSearchActivity.GetTagItem("mcpgateway.search.cache_hit") == true)
            .IsTrue();
        await Assert
            .That(cachedSearchActivity.GetTagItem("mcpgateway.search.vector_duration_ms"))
            .IsNull();
        await Assert
            .That(cachedSearchActivity.GetTagItem("mcpgateway.search.vector_tokens"))
            .IsNull();
        await Assert
            .That(cachedSearchActivity.GetTagItem("mcpgateway.search.graph_duration_ms"))
            .IsNull();

        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.requests"
                    && (bool?)measurement.Tags["mcpgateway.search.cache_hit"] == true
                )
            )
            .IsTrue();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.vector.duration"
                    && (bool?)measurement.Tags["mcpgateway.search.cache_hit"] == true
                )
            )
            .IsFalse();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.vector.tokens"
                    && (bool?)measurement.Tags["mcpgateway.search.cache_hit"] == true
                )
            )
            .IsFalse();
        await Assert
            .That(
                measurements.Any(static measurement =>
                    measurement.InstrumentName == "mcpgateway.search.graph.duration"
                    && (bool?)measurement.Tags["mcpgateway.search.cache_hit"] == true
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_BrowseCacheInvalidatesAfterIndexRebuild()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            options =>
            {
                options.AddTool(
                    "local",
                    TestFunctionFactory.CreateFunction(
                        SearchGitHub,
                        "github_search_issues",
                        "Search GitHub issues and pull requests by user query."
                    )
                );
            },
            useInMemorySearchCache: true
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await gateway.BuildIndexAsync();
        var firstBrowse = await gateway.SearchAsync(query: null, maxResults: 2);

        registry.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "weather_search_forecast",
                "Search weather forecast and temperature information by city name."
            )
        );

        await gateway.BuildIndexAsync();
        var secondBrowse = await gateway.SearchAsync(query: null, maxResults: 2);

        await Assert.That(firstBrowse.RankingMode).IsEqualTo("browse");
        await Assert.That(firstBrowse.Matches.Count).IsEqualTo(1);
        await Assert.That(secondBrowse.RankingMode).IsEqualTo("browse");
        await Assert.That(secondBrowse.Matches.Count).IsEqualTo(2);
        await Assert
            .That(
                secondBrowse.Matches.Any(static match =>
                    match.ToolId == "local:weather_search_forecast"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_SharedSearchCacheSeparatesNormalizedQueriesByChatClientFingerprint()
    {
        using var sharedSearchCache = new McpGatewayInMemorySearchCache();
        var notificationChatClient = new TestChatClient(
            new TestChatClientOptions
            {
                Metadata = new ChatClientMetadata(
                    "ManagedCode.MCPGateway.Tests",
                    new Uri("https://example.test/chat/notifications"),
                    "rewriter-notifications"
                ),
                RewriteQuery = static _ => "notification inbox alerts unread activity",
            }
        );
        var familyChatClient = new TestChatClient(
            new TestChatClientOptions
            {
                Metadata = new ChatClientMetadata(
                    "ManagedCode.MCPGateway.Tests",
                    new Uri("https://example.test/chat/family"),
                    "rewriter-family"
                ),
                RewriteQuery = static _ => "family tree parent relationship person",
            }
        );

        await using var notificationProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoNotificationAndFamilyTools,
            CreateNotificationAutoEmbeddingGenerator(),
            searchQueryChatClient: notificationChatClient,
            searchCache: sharedSearchCache
        );
        await using var familyProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoNotificationAndFamilyTools,
            CreateNotificationAutoEmbeddingGenerator(),
            searchQueryChatClient: familyChatClient,
            searchCache: sharedSearchCache
        );

        var notificationGateway = notificationProvider.GetRequiredService<IMcpGateway>();
        var familyGateway = familyProvider.GetRequiredService<IMcpGateway>();

        await notificationGateway.BuildIndexAsync();
        await familyGateway.BuildIndexAsync();

        var originalQuery = "а що там взагалі знайти";
        var notificationSearch = await notificationGateway.SearchAsync(
            originalQuery,
            maxResults: 1
        );
        var familySearch = await familyGateway.SearchAsync(originalQuery, maxResults: 1);

        await Assert
            .That(notificationSearch.Matches[0].ToolId)
            .IsEqualTo("local:notification_activity_search");
        await Assert.That(notificationChatClient.Calls.Count).IsEqualTo(1);
        await Assert.That(familyChatClient.Calls.Count).IsEqualTo(1);
        await Assert
            .That(
                familySearch.Matches[0].ToolId == "local:storied_person_add_father"
                    || familySearch.Matches[0].ToolId == "local:storied_person_add_mother"
                    || familySearch.Matches[0].ToolId == "local:storied_person_add_potential_parent"
            )
            .IsTrue();
    }
}
