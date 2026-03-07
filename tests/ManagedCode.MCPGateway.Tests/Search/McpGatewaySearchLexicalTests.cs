using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
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
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
            options.AddTool("local", TestFunctionFactory.CreateFunction(FilterAdvisories, "advisory_lookup", "Lookup advisory records."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("severity filter", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:advisory_lookup");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_DefaultAutoStrategyUsesTokenizerFallbackAndTopFiveLimit()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureDefaultAutoTokenizerFallbackTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var searchResult = await gateway.SearchAsync("search");

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "lexical_fallback")).IsTrue();
        await Assert.That(searchResult.Matches.Count).IsEqualTo(5);
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_DefaultAutoStrategyHandlesTypoHeavyQueryWithoutEmbeddings()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureDefaultAutoTokenizerFallbackTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var searchResult = await gateway.SearchAsync("track shipmnt 1z999");

        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "lexical_fallback")).IsTrue();
        await Assert.That(searchResult.Matches.Any(static match => match.ToolId == "local:commerce_shipping_tracking")).IsTrue();
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

    private static void ConfigureDefaultAutoTokenizerFallbackTools(McpGatewayOptions options)
    {
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "weather_air_quality_lookup", "Lookup air quality index, smoke exposure, and pollution levels for a location."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "commerce_shipping_tracking", "Track shipment status, carrier events, and delivery estimates."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "finance_invoice_search", "Find invoices by customer, invoice number, payment state, or due date."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "crm_contact_search", "Find CRM contacts by name, email, title, account, or segment."));
    }
}
