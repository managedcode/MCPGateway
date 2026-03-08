using System.Globalization;

using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

internal static class GatewayIntegrationTestSupport
{
    public const int CatalogToolCount = 50;
    public const string WeatherToolName = "weather_forecast_specialist";
    public const string WeatherAirQualityToolName = "weather_air_quality_watch";
    public const string PortfolioToolName = "portfolio_status_specialist";
    public const string InvoiceToolName = "finance_invoice_watch";
    public const string WeatherToolId = $"local:{WeatherToolName}";
    public const string WeatherAirQualityToolId = $"local:{WeatherAirQualityToolName}";
    public const string PortfolioToolId = $"local:{PortfolioToolName}";
    public const string InvoiceToolId = $"local:{InvoiceToolName}";
    public const string WeatherInvokeQuery = "Kyiv";
    public const string PortfolioInvokeQuery = "ACME";
    public const string FinalAssistantResponse = "done:weather:Kyiv|portfolio:ACME";
    private const string WeatherLexicalQuery = "kyiv weather forecast";
    private const string WeatherSemanticQuery = "umbrella planning for kyiv";
    private const string PortfolioLexicalQuery = "portfolio market value exposure billing";
    private const string PortfolioSemanticQuery = "brokerage holdings snapshot";

    public static void ConfigureFiftyToolCatalog(McpGatewayOptions options)
    {
        for (var index = 1; index <= CatalogToolCount; index++)
        {
            switch (index)
            {
                case 37:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            (string query) => $"weather:{query}",
                            WeatherToolName,
                            "Get weather forecast, temperature, wind, and precipitation details for a city."));
                    break;
                case 38:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            (string query) => $"air-quality:{query}",
                            WeatherAirQualityToolName,
                            "Check weather alerts, air quality, smoke exposure, and pollution levels for a city."));
                    break;
                case 41:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            (string query) => $"portfolio:{query}",
                            PortfolioToolName,
                            "Summarize portfolio holdings, market value, exposure, and unrealized profit for a brokerage account."));
                    break;
                case 42:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            (string query) => $"invoice:{query}",
                            InvoiceToolName,
                            "Review finance invoices, billing exposure, receivables, and payment holds for a customer account."));
                    break;
                default:
                    var toolIndex = index.ToString("D2", CultureInfo.InvariantCulture);
                    var toolName = $"catalog_tool_{toolIndex}";
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            (string query) => $"{toolName}:{query}",
                            toolName,
                            $"Handle archive lookup workflow number {toolIndex} for genealogy records."));
                    break;
            }
        }
    }

    public static TestEmbeddingGenerator CreateAutoDiscoveryEmbeddingGenerator()
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "gateway-autodiscovery",
                4),
            CreateVector = CreateSemanticVector
        });

    public static IReadOnlyList<TestChatClientScenario> CreateAutoDiscoveryScenarios(bool useSemanticQueries)
    {
        var firstSearchQuery = useSemanticQueries ? WeatherSemanticQuery : WeatherLexicalQuery;
        var secondSearchQuery = useSemanticQueries ? PortfolioSemanticQuery : PortfolioLexicalQuery;

        return
        [
            new(
                "search weather tools",
                invocation => invocation.CountFunctionResults(McpGatewayToolSet.DefaultSearchToolName) == 0,
                _ => TestChatClientScenario.FunctionCall(
                    callId: "search-weather",
                    functionName: McpGatewayToolSet.DefaultSearchToolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["query"] = firstSearchQuery,
                        ["maxResults"] = 2
                    })),
            new(
                "invoke discovered weather tool",
                invocation => invocation.CountFunctionResults(McpGatewayToolSet.DefaultSearchToolName) == 1 &&
                              invocation.CountFunctionResults(WeatherToolName) == 0,
                _ => TestChatClientScenario.FunctionCall(
                    callId: "invoke-weather",
                    functionName: WeatherToolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["query"] = WeatherInvokeQuery
                    })),
            new(
                "search finance tools",
                invocation => invocation.CountFunctionResults(WeatherToolName) == 1 &&
                              invocation.CountFunctionResults(McpGatewayToolSet.DefaultSearchToolName) == 1,
                _ => TestChatClientScenario.FunctionCall(
                    callId: "search-portfolio",
                    functionName: McpGatewayToolSet.DefaultSearchToolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["query"] = secondSearchQuery,
                        ["maxResults"] = 2
                    })),
            new(
                "invoke discovered portfolio tool",
                invocation => invocation.CountFunctionResults(McpGatewayToolSet.DefaultSearchToolName) == 2 &&
                              invocation.CountFunctionResults(PortfolioToolName) == 0,
                _ => TestChatClientScenario.FunctionCall(
                    callId: "invoke-portfolio",
                    functionName: PortfolioToolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["query"] = PortfolioInvokeQuery
                    })),
            new(
                "return final text",
                invocation => invocation.CountFunctionResults(PortfolioToolName) == 1,
                invocation =>
                {
                    var weatherResult = invocation.ReadLatestFunctionResult<McpGatewayInvokeResult>(WeatherToolName)
                                        ?? throw new InvalidOperationException("Weather result is missing.");
                    var portfolioResult = invocation.ReadLatestFunctionResult<McpGatewayInvokeResult>(PortfolioToolName)
                                          ?? throw new InvalidOperationException("Portfolio result is missing.");

                    return TestChatClientScenario.Text($"done:{weatherResult.Output}|{portfolioResult.Output}");
                })
        ];
    }

    public static async Task AssertAutoDiscoveryFlow(
        TestChatClient chatClient,
        string expectedRankingMode)
    {
        await Assert.That(chatClient.Invocations.Count).IsEqualTo(5);
        await Assert.That(chatClient.Invocations[0].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName
            ]);
        await Assert.That(chatClient.Invocations[1].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
                WeatherToolName,
                WeatherAirQualityToolName
            ]);
        await Assert.That(chatClient.Invocations[2].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
                WeatherToolName,
                WeatherAirQualityToolName
            ]);
        await Assert.That(chatClient.Invocations[3].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
                PortfolioToolName,
                InvoiceToolName
            ]);
        await Assert.That(chatClient.Invocations[4].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
                PortfolioToolName,
                InvoiceToolName
            ]);

        var firstSearchResult = chatClient.Invocations[1].ReadLatestFunctionResult<McpGatewaySearchResult>(
                                    McpGatewayToolSet.DefaultSearchToolName)
                                ?? throw new InvalidOperationException("First search result is missing.");
        var secondSearchResult = chatClient.Invocations[3].ReadLatestFunctionResult<McpGatewaySearchResult>(
                                     McpGatewayToolSet.DefaultSearchToolName)
                                 ?? throw new InvalidOperationException("Second search result is missing.");

        await Assert.That(firstSearchResult.RankingMode).IsEqualTo(expectedRankingMode);
        await Assert.That(firstSearchResult.Matches[0].ToolId).IsEqualTo(WeatherToolId);
        await Assert.That(firstSearchResult.Matches[1].ToolId).IsEqualTo(WeatherAirQualityToolId);
        await Assert.That(secondSearchResult.RankingMode).IsEqualTo(expectedRankingMode);
        await Assert.That(secondSearchResult.Matches[0].ToolId).IsEqualTo(PortfolioToolId);
        await Assert.That(secondSearchResult.Matches[1].ToolId).IsEqualTo(InvoiceToolId);

        foreach (var invocation in chatClient.Invocations)
        {
            await Assert.That(invocation.FindLatestFunctionResult(McpGatewayToolSet.DefaultInvokeToolName)).IsNull();
        }
    }

    private static float[] CreateSemanticVector(string value)
    {
        var normalized = value.ToLowerInvariant();
        var vector = new float[4];

        vector[0] = Score(normalized, "weather", "forecast", "temperature", "umbrella", "rain", "wind", "precipitation");
        vector[1] = Score(normalized, "air quality", "pollution", "smoke", "aqi", "alerts");
        vector[2] = Score(normalized, "portfolio", "brokerage", "holdings", "exposure", "market value", "profit", "snapshot");
        vector[3] = Score(normalized, "invoice", "billing", "receivables", "payment", "collections");

        return vector;
    }

    private static float Score(string normalized, params string[] keywords)
    {
        var score = 0f;
        foreach (var keyword in keywords)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                score += 1f;
            }
        }

        return score;
    }
}
