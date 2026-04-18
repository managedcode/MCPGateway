using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyUsesVectorPrimaryRankingBeforeGraphSupplements()
    {
        var embeddingGenerator = CreateAutoSemanticEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("weather forecast by city", maxResults: 2);

        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "hybrid_vector_merge_used")).IsFalse();
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "graph_fallback")).IsFalse();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
        await Assert.That(searchResult.FocusedGraphNodeCount).IsGreaterThan(0);
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert.That(embeddingGenerator.Calls[1].Single()).IsEqualTo("weather forecast by city");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyFallsBackToGraphWhenVectorQueryIsUnusable()
    {
        var embeddingGenerator = CreateAutoSemanticEmbeddingGenerator(
            returnZeroVectorOnQuery: static value => string.Equals(value, "weather forecast by city", StringComparison.Ordinal));
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("weather forecast by city", maxResults: 2);

        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "query_vector_empty")).IsTrue();
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "graph_fallback")).IsTrue();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyPreservesOriginalAndNormalizedQueryForMultilingualVectorRanking()
    {
        var chatClient = CreateNotificationsRewriteClient();
        var embeddingGenerator = CreateNotificationAutoEmbeddingGenerator();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoNotificationAndFamilyTools,
            embeddingGenerator,
            searchQueryChatClient: chatClient);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 2);

        await Assert.That(searchResult.RankingMode is "vector" or "hybrid").IsTrue();
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "query_normalized")).IsTrue();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:notification_activity_search");
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert.That(embeddingGenerator.Calls[1].Single()).Contains("а що у мен з нотіфікешенми");
        await Assert.That(embeddingGenerator.Calls[1].Single()).Contains("english query: notification inbox alerts unread activity");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyAddsBoundedGraphSupplementsAfterVectorPrimaryRanking()
    {
        var embeddingGenerator = CreateStoryAutoEmbeddingGenerator();

        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
            {
                options.SearchStrategy = McpGatewaySearchStrategy.Auto;
                options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
            },
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("search story feed items before detail lookup or comments", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("hybrid");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "hybrid_vector_merge_used")).IsTrue();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("graph-mcp:story_item_search");
        await Assert.That(searchResult.RelatedMatches.Count).IsGreaterThan(0);
        await Assert.That(
            searchResult.RelatedMatches.Any(static match => match.ToolId == "graph-mcp:story_item_detail") ||
            searchResult.NextStepMatches.Any(static match => match.ToolId == "graph-mcp:story_item_detail")).IsTrue();
        await Assert.That(searchResult.RelatedMatches.Any(static match => match.ToolId == "graph-mcp:people_profile_search")).IsFalse();
        await Assert.That(searchResult.NextStepMatches.Any(static match => match.ToolId == "graph-mcp:people_profile_search")).IsFalse();
    }

    private static void ConfigureAutoSearchTools(McpGatewayOptions options)
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Auto;
        ConfigureSearchTools(options);
    }

    private static void ConfigureAutoNotificationAndFamilyTools(McpGatewayOptions options)
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Auto;
        ConfigureNotificationAndFamilyTools(options);
    }

    private static TestEmbeddingGenerator CreateAutoSemanticEmbeddingGenerator(Func<string, bool>? returnZeroVectorOnQuery = null)
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "auto-semantic",
                2),
            CreateVector = value =>
            {
                if (returnZeroVectorOnQuery?.Invoke(value) == true)
                {
                    return [0f, 0f];
                }

                var normalized = value.ToLowerInvariant();
                return
                [
                    ScoreSemanticTerms(normalized, "weather", "forecast", "temperature", "city", "umbrella", "rain", "commute"),
                    ScoreSemanticTerms(normalized, "github", "pull", "request", "issue", "repository", "merge", "review")
                ];
            }
        });

    private static TestEmbeddingGenerator CreateNotificationAutoEmbeddingGenerator()
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "auto-notifications",
                2),
            CreateVector = static value =>
            {
                var normalized = value.ToLowerInvariant();
                return
                [
                    ScoreSemanticTerms(normalized, "notification", "notifications", "alerts", "unread", "activity", "inbox", "mentions"),
                    ScoreSemanticTerms(normalized, "father", "mother", "parent", "tree", "person", "family", "relationship")
                ];
            }
        });

    private static TestEmbeddingGenerator CreateStoryAutoEmbeddingGenerator()
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "auto-story",
                3),
            CreateVector = static value =>
            {
                var normalized = value.ToLowerInvariant();
                return
                [
                    ScoreSemanticTerms(normalized, "search story feed items", "story feed", "query text", "search", "search", "query", "items"),
                    ScoreSemanticTerms(normalized, "detail", "comments", "comment", "detail view"),
                    ScoreSemanticTerms(normalized, "people", "profile", "person")
                ];
            }
        });

    private static float ScoreSemanticTerms(string normalized, params string[] terms)
    {
        var score = 0f;
        foreach (var term in terms)
        {
            if (normalized.Contains(term, StringComparison.Ordinal))
            {
                score += 1f;
            }
        }

        return score;
    }
}
