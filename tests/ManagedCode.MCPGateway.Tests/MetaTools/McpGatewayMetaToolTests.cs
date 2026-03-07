using System.ComponentModel;
using System.Text.Json;

using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMetaToolTests
{
    [TUnit.Core.Test]
    public async Task CreateMetaTools_SearchToolSupportsContextAwareRequests()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));
        }, embeddingGenerator);

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var searchTool = GetFunction(gateway.CreateMetaTools(), McpGatewayToolSet.DefaultSearchToolName);

        var result = await searchTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "search",
                    ["contextSummary"] = "weather forecast"
                },
                StringComparer.OrdinalIgnoreCase));

        await Assert.That(result).IsTypeOf<JsonElement>();

        var searchResult = (JsonElement)result!;
        var matches = GetJsonProperty(searchResult, "matches");
        await Assert.That(matches[0].ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(GetJsonProperty(matches[0], "toolId").GetString()).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task CreateMetaTools_InvokeToolSupportsContextSummary()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(EchoContextSummary, "context_summary_echo", "Echo query and context summary."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeTool = GetFunction(gateway.CreateMetaTools(), McpGatewayToolSet.DefaultInvokeToolName);
        var result = await invokeTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["toolId"] = "local:context_summary_echo",
                    ["query"] = "open github",
                    ["contextSummary"] = "user is on repository settings page"
                },
                StringComparer.OrdinalIgnoreCase));

        await Assert.That(result).IsTypeOf<JsonElement>();

        var invokeResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(invokeResult, "isSuccess").GetBoolean()).IsTrue();
        await Assert.That(GetJsonProperty(invokeResult, "output").GetString()).IsEqualTo("open github|user is on repository settings page");
    }

    private static AIFunction GetFunction(IReadOnlyList<AITool> tools, string toolName)
        => (tools.Single(tool => tool.Name == toolName) as AIFunction)
           ?? throw new InvalidOperationException($"Tool '{toolName}' is not an AIFunction.");

    private static string SearchGitHub([Description("Search query text.")] string query) => $"github:{query}";

    private static string SearchWeather([Description("City or weather request text.")] string query) => $"weather:{query}";

    private static string EchoContextSummary(
        [Description("Main query text.")] string query,
        [Description("Execution context summary.")] string contextSummary)
        => $"{query}|{contextSummary}";

    private static JsonElement GetJsonProperty(JsonElement element, string name)
        => element.EnumerateObject()
            .First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;
}
