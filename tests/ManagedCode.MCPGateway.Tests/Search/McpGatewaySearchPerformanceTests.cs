using System.Diagnostics;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    private const int PerformanceCatalogToolCount = 120;

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_AutoStrategyLargeCatalogPerformanceSmoke()
    {
        var embeddingGenerator = CreatePerformanceEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigurePerformanceCatalog,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var stopwatch = Stopwatch.StartNew();
        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(PerformanceCatalogToolCount);
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(PerformanceCatalogToolCount);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingGenerator.Calls[0].Count).IsEqualTo(PerformanceCatalogToolCount);
        await Assert.That(stopwatch.Elapsed < TimeSpan.FromSeconds(20)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_AutoStrategyRepeatedQueriesReuseBuiltIndexAndRuntimeCachesPerformanceSmoke()
    {
        var embeddingGenerator = CreatePerformanceEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigurePerformanceCatalog,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();

        _ = await gateway.SearchAsync("umbrella planning for region seven", maxResults: 1);

        var queries = Enumerable.Range(0, 8)
            .Select(index => index % 2 == 0
                ? "umbrella planning for region seven"
                : "brokerage holdings snapshot for acme")
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        foreach (var query in queries)
        {
            var searchResult = await gateway.SearchAsync(query, maxResults: 1);
            var expectedToolId = query.Contains("umbrella", StringComparison.Ordinal)
                ? "local:weather_dispatch_specialist"
                : "local:portfolio_status_specialist";

            await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo(expectedToolId);
        }

        await Assert.That(stopwatch.Elapsed < TimeSpan.FromSeconds(8)).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(3);
        await Assert.That(embeddingGenerator.Calls.Count(static call => call.Count == PerformanceCatalogToolCount)).IsEqualTo(1);
        await Assert.That(embeddingGenerator.Calls.Skip(1).All(static call => call.Count == 1)).IsTrue();
    }

    private static void ConfigurePerformanceCatalog(McpGatewayOptions options)
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Auto;

        for (var index = 1; index <= PerformanceCatalogToolCount; index++)
        {
            switch (index)
            {
                case 81:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            static (string query) => $"weather-dispatch:{query}",
                            "weather_dispatch_specialist",
                            "Get weather forecast, rain, wind, and precipitation details for a city or region."));
                    break;
                case 82:
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            static (string query) => $"portfolio-status:{query}",
                            "portfolio_status_specialist",
                            "Summarize brokerage holdings, market value, and exposure for an investment account."));
                    break;
                default:
                    var suffix = index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                    options.AddTool(
                        "local",
                        TestFunctionFactory.CreateFunction(
                            static (string query) => $"archive:{query}",
                            $"archive_lookup_{suffix}",
                            $"Handle archive lookup workflow number {suffix} for genealogy records."));
                    break;
            }
        }
    }

    private static TestEmbeddingGenerator CreatePerformanceEmbeddingGenerator()
        => new(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "performance-smoke",
                3),
            CreateVector = static value =>
            {
                var normalized = value.ToLowerInvariant();
                return
                [
                    ScoreSemanticTerms(normalized, "weather", "forecast", "umbrella", "rain", "wind", "precipitation", "city", "region"),
                    ScoreSemanticTerms(normalized, "portfolio", "brokerage", "holdings", "market value", "exposure", "investment", "snapshot"),
                    ScoreSemanticTerms(normalized, "archive", "genealogy", "workflow", "lookup", "records")
                ];
            }
        });
}
