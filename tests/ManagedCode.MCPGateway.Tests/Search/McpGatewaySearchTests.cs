using System.ComponentModel;

using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySearchTests
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
    public async Task SearchAsync_UsesContextDictionaryForLexicalFallback()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSearchTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(new McpGatewaySearchRequest(
            Query: "search",
            MaxResults: 2,
            Context: new Dictionary<string, object?>
            {
                ["page"] = "weather forecast",
                ["intent"] = "temperature lookup"
            }));

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "lexical_fallback")).IsTrue();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesSchemaTermsForLexicalFallback()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
            options.AddTool("local", CreateFunction(FilterAdvisories, "advisory_lookup", "Lookup advisory records."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("severity filter", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:advisory_lookup");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesBrowseModeWhenQueryAndContextAreMissing()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSearchTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(new McpGatewaySearchRequest());

        await Assert.That(searchResult.RankingMode).IsEqualTo("browse");
        await Assert.That(searchResult.Matches.Count).IsEqualTo(2);
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
        await Assert.That(searchResult.Matches[1].ToolId).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_FallsBackWhenQueryEmbeddingFails()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ThrowOnInput = static input => input.Contains("explode query", StringComparison.Ordinal)
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("explode query", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "vector_search_failed")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_FallsBackWhenQueryVectorIsEmpty()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ReturnZeroVectorOnInput = static input => input.Contains("empty query vector", StringComparison.Ordinal)
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("empty query vector", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "query_vector_empty")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingCountMismatch()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ReturnMismatchedBatchCount = true
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 1);

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_count_mismatch")).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingFailure()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ThrowOnInput = static _ => true
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_failed")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_SkipsDuplicateToolIds()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
            options.AddTool("local", CreateFunction(SearchGitHubAgain, "github_search_issues", "Duplicate tool id for test coverage."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(1);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "duplicate_tool_id")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RebuildsAfterNewToolIsRegistered()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        var firstBuild = await gateway.BuildIndexAsync();

        registry.AddTool(
            "local",
            CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));

        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(firstBuild.ToolCount).IsEqualTo(1);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RetriesFailedMcpClientFactoryOnNextBuild()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();

        var attempts = 0;
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClientFactory(
                "test-mcp",
                async _ =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new InvalidOperationException("temporary startup failure");
                    }

                    return serverHost.Client;
                },
                disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var firstBuild = await gateway.BuildIndexAsync();
        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(firstBuild.ToolCount).IsEqualTo(0);
        await Assert.That(firstBuild.Diagnostics.Any(static diagnostic => diagnostic.Code == "source_load_failed")).IsTrue();
        await Assert.That(secondBuild.ToolCount).IsEqualTo(3);
        await Assert.That(secondBuild.Diagnostics.Any(static diagnostic => diagnostic.Code == "source_load_failed")).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_BuildsIndexOnDemand()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSearchTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();

        await Assert.That(tools.Count).IsEqualTo(2);
        await Assert.That(tools.Any(static tool => tool.ToolId == "local:github_search_issues")).IsTrue();
        await Assert.That(tools.Any(static tool => tool.ToolId == "local:weather_search_forecast")).IsTrue();
    }

    private static void ConfigureSearchTools(McpGatewayOptions options)
    {
        options.AddTool("local", CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        options.AddTool("local", CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));
    }

    private static AIFunction CreateFunction(Delegate callback, string name, string description)
        => AIFunctionFactory.Create(
            callback,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });

    private static string SearchGitHub([Description("Search query text.")] string query) => $"github:{query}";

    private static string SearchGitHubAgain([Description("Search query text.")] string query) => $"github-duplicate:{query}";

    private static string SearchWeather([Description("City or weather request text.")] string query) => $"weather:{query}";

    private static string FilterAdvisories([Description("Severity filter to apply to advisory lookups.")] string severity)
        => $"advisory:{severity}";
}
