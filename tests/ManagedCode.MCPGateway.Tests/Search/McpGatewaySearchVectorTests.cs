using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_VectorizesToolDescriptorsAndCapturesSemanticDocuments()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(2);
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingGenerator.Calls[0].Count).IsEqualTo(2);
        await Assert.That(embeddingGenerator.Calls[0].Any(static text =>
            text.Contains("github_search_issues", StringComparison.Ordinal) &&
            text.Contains("Search GitHub issues and pull requests by user query.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(embeddingGenerator.Calls[0].Any(static text =>
            text.Contains("weather_search_forecast", StringComparison.Ordinal) &&
            text.Contains("Search weather forecast and temperature information by city name.", StringComparison.Ordinal))).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_RanksLocalToolsWithEmbeddings()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 2);

        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
        await Assert.That(searchResult.Matches[0].Score >= searchResult.Matches[1].Score).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert.That(embeddingGenerator.Calls[1].Single()).IsEqualTo("github pull requests");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesContextSummaryInEmbeddingQuery()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(new McpGatewaySearchRequest(
            Query: "search",
            MaxResults: 2,
            ContextSummary: "github pull requests"));

        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
        await Assert.That(embeddingGenerator.Calls[1].Single().Contains("context summary: github pull requests", StringComparison.Ordinal)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesContextOnlyInputWhenQueryIsMissing()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(new McpGatewaySearchRequest(
            ContextSummary: "weather forecast",
            MaxResults: 1));

        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
        await Assert.That(embeddingGenerator.Calls[1].Single()).IsEqualTo("context summary: weather forecast");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_PrefersKeyedEmbeddingGeneratorOverUnkeyedRegistration()
    {
        var keyedEmbeddingGenerator = new TestEmbeddingGenerator();
        var fallbackEmbeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ThrowOnInput = static _ => true
        });

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(fallbackEmbeddingGenerator);
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            McpGatewayServiceKeys.EmbeddingGenerator,
            keyedEmbeddingGenerator);
        services.AddManagedCodeMcpGateway(ConfigureSearchTools);

        await using var serviceProvider = services.BuildServiceProvider();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 2);

        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
        await Assert.That(keyedEmbeddingGenerator.Calls.Count).IsEqualTo(2);
        await Assert.That(fallbackEmbeddingGenerator.Calls.Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_ResolvesScopedEmbeddingGeneratorPerOperation()
    {
        var tracker = new ScopedEmbeddingGeneratorTracker();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddScoped<IEmbeddingGenerator<string, Embedding<float>>>(_ => new ScopedTestEmbeddingGenerator(tracker));
        services.AddManagedCodeMcpGateway(ConfigureSearchTools);

        await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 2);

        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("vector");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
        await Assert.That(tracker.InstanceIds.Distinct().Count()).IsEqualTo(2);
        await Assert.That(tracker.Calls.Count).IsEqualTo(2);
    }
}
