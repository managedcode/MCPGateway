using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyKeepsGraphResultsWhenGraphConfidenceIsHigh()
    {
        var embeddingGenerator = CreateAutoSemanticEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("weather forecast by city", maxResults: 2);

        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "hybrid_vector_merge_used")).IsFalse();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(1);
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyUsesHybridSemanticRescueWhenGraphConfidenceIsLow()
    {
        var embeddingGenerator = CreateAutoSemanticEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureAutoSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("umbrella planning for the commute", maxResults: 2);

        await Assert.That(searchResult.RankingMode).IsEqualTo("hybrid");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "low_confidence_results")).IsTrue();
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "hybrid_vector_merge_used")).IsTrue();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
        await Assert.That(searchResult.Matches[0].Score).IsGreaterThan(0.5d);
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert.That(embeddingGenerator.Calls[1].Single()).IsEqualTo("umbrella planning for the commute");
    }

    private static void ConfigureAutoSearchTools(McpGatewayOptions options)
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Auto;
        ConfigureSearchTools(options);
    }

    private static TestEmbeddingGenerator CreateAutoSemanticEmbeddingGenerator()
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "auto-semantic",
                2),
            CreateVector = static value =>
            {
                var normalized = value.ToLowerInvariant();
                return
                [
                    ScoreSemanticTerms(normalized, "weather", "forecast", "temperature", "city", "umbrella", "rain", "commute"),
                    ScoreSemanticTerms(normalized, "github", "pull", "request", "issue", "repository", "merge", "review")
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
