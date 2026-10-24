using System.Globalization;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

/// <summary>
/// Gateway instances in one process that build a generated tool graph for an identical catalog share one immutable
/// graph index. Every test owns an isolated pool so its build counts cannot be affected by parallel tests. Index
/// equivalence is proven on the exported graph (JSON-LD and Turtle) plus primary search results: under concurrent
/// load the Markdown-LD SPARQL engine can return different secondary evidence for one query on one graph object,
/// so complete result sets are not a stable equality oracle.
/// </summary>
public sealed class McpGatewayGraphIndexReuseTests
{
    private const string WeatherDescription = "Search weather forecast, rain and temperature by city name.";
    private const int SearchLimit = 3;
    private static readonly (string Query, string ToolId)[] PrimaryQueries =
    [
        ("weather forecast for Paris", "weather_search_forecast"),
        ("refund a paid invoice", "billing_refund_invoice"),
        ("create a github issue in a repository", "github_create_issue"),
    ];

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_IdenticalCatalogsBuildOneGraphThatSearchesLikeAFreshBuild()
    {
        var pool = CreatePool();
        await using var first = CreateProvider(pool, options => ConfigureCatalog(options, "alpha"));
        await using var second = CreateProvider(pool, options => ConfigureCatalog(options, "beta"));
        await using var fresh = CreateProvider(CreatePool(), options => ConfigureCatalog(options, "fresh"));
        var firstGateway = first.GetRequiredService<IMcpGateway>();
        var secondGateway = second.GetRequiredService<IMcpGateway>();
        var freshGateway = fresh.GetRequiredService<IMcpGateway>();

        var firstBuild = await firstGateway.BuildIndexAsync();
        var secondBuild = await secondGateway.BuildIndexAsync();
        var freshBuild = await freshGateway.BuildIndexAsync();

        await Assert.That(firstBuild.IsGraphIndexReused).IsFalse();
        await Assert.That(secondBuild.IsGraphIndexReused).IsTrue();
        await Assert.That(freshBuild.IsGraphIndexReused).IsFalse();
        await Assert.That(pool.BuildCount).IsEqualTo(1);
        await Assert.That(pool.Count).IsEqualTo(1);
        await Assert.That(secondBuild.GraphNodeCount).IsEqualTo(firstBuild.GraphNodeCount);
        await Assert.That(secondBuild.GraphEdgeCount).IsEqualTo(firstBuild.GraphEdgeCount);
        await Assert.That(secondBuild.IsGraphSearchEnabled).IsTrue();

        var expectedGraph = await ExportGraphAsync(freshGateway);
        await Assert.That(await ExportGraphAsync(firstGateway)).IsEqualTo(expectedGraph);
        await Assert.That(await ExportGraphAsync(secondGateway)).IsEqualTo(expectedGraph);
        await AssertPrimaryMatchesAsync(freshGateway);
        await AssertPrimaryMatchesAsync(firstGateway);
        await AssertPrimaryMatchesAsync(secondGateway);
        var noMatch = await secondGateway.SearchAsync("satellite coffee roasting", SearchLimit);
        await Assert.That(noMatch.RankingMode).IsEqualTo((await freshGateway.SearchAsync("satellite coffee roasting", SearchLimit)).RankingMode);
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_SharedGraphStillExecutesEachInstanceOwnTool()
    {
        var pool = CreatePool();
        await using var first = CreateProvider(pool, options => ConfigureCatalog(options, "alpha"));
        await using var second = CreateProvider(pool, options => ConfigureCatalog(options, "beta"));
        var firstGateway = first.GetRequiredService<IMcpGateway>();
        var secondGateway = second.GetRequiredService<IMcpGateway>();
        _ = await firstGateway.BuildIndexAsync();
        var secondBuild = await secondGateway.BuildIndexAsync();

        var firstSearch = await firstGateway.SearchAsync("weather forecast for Paris", SearchLimit);
        var secondSearch = await secondGateway.SearchAsync("weather forecast for Paris", SearchLimit);
        var firstResult = await firstGateway.InvokeAsync(
            new McpGatewayInvokeRequest(ToolId: firstSearch.Matches[0].ToolId, Query: "Paris"));
        var secondResult = await secondGateway.InvokeAsync(
            new McpGatewayInvokeRequest(ToolId: secondSearch.Matches[0].ToolId, Query: "Paris"));

        await Assert.That(secondBuild.IsGraphIndexReused).IsTrue();
        await Assert.That(firstResult.IsSuccess).IsTrue();
        await Assert.That(secondResult.IsSuccess).IsTrue();
        await Assert.That((string)firstResult.Output!).IsEqualTo("alpha:weather_search_forecast:Paris");
        await Assert.That((string)secondResult.Output!).IsEqualTo("beta:weather_search_forecast:Paris");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ChangedDescriptionSchemaOrHintsBuildSeparateGraphs()
    {
        var pool = CreatePool();
        await using var original = CreateProvider(pool, options => ConfigureCatalog(options, "alpha"));
        await using var described = CreateProvider(pool, options => ConfigureCatalog(options, "alpha",
            weatherDescription: "Search marine tide tables and surf swell by harbor."));
        await using var reshaped = CreateProvider(pool, options => ConfigureCatalog(options, "alpha", issueLimit: true));
        await using var hinted = CreateProvider(pool, options => ConfigureCatalog(options, "alpha", billingAlias: "receipt lookup"));

        var builds = new[]
        {
            await original.GetRequiredService<IMcpGateway>().BuildIndexAsync(),
            await described.GetRequiredService<IMcpGateway>().BuildIndexAsync(),
            await reshaped.GetRequiredService<IMcpGateway>().BuildIndexAsync(),
            await hinted.GetRequiredService<IMcpGateway>().BuildIndexAsync(),
        };
        var describedSearch = await described.GetRequiredService<IMcpGateway>().SearchAsync("tide tables for a harbor", 1);
        var originalSearch = await original.GetRequiredService<IMcpGateway>().SearchAsync(WeatherDescription, 1);

        await Assert.That(builds.Count(static build => build.IsGraphIndexReused)).IsEqualTo(0);
        await Assert.That(pool.BuildCount).IsEqualTo(4);
        await Assert.That(pool.Count).IsEqualTo(4);
        await Assert.That(describedSearch.Matches[0].Description).Contains("tide tables");
        await Assert.That(originalSearch.Matches[0].Description).IsEqualTo(WeatherDescription);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ConcurrentIdenticalCatalogsCoalesceIntoOneGraphBuild()
    {
        const int hostCount = 6;
        var pool = CreatePool();
        var providers = Enumerable.Range(0, hostCount)
            .Select(index => CreateProvider(pool, options => ConfigureCatalog(options, $"host{index}")))
            .ToArray();
        try
        {
            var builds = await Task.WhenAll(providers.Select(static provider =>
                Task.Run(() => provider.GetRequiredService<IMcpGateway>().BuildIndexAsync())));

            await Assert.That(pool.BuildCount).IsEqualTo(1);
            await Assert.That(builds.Count(static build => !build.IsGraphIndexReused)).IsEqualTo(1);
            await Assert.That(builds.Select(static build => build.GraphNodeCount).Distinct().Count()).IsEqualTo(1);
            var graphs = await Task.WhenAll(providers.Select(static provider =>
                ExportGraphAsync(provider.GetRequiredService<IMcpGateway>())));
            await Assert.That(graphs.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);
            await Task.WhenAll(providers.Select(static provider =>
                AssertPrimaryMatchesAsync(provider.GetRequiredService<IMcpGateway>())));
        }
        finally
        {
            foreach (var provider in providers)
            {
                await provider.DisposeAsync();
            }
        }
    }

    [TUnit.Core.Test]
    public async Task DisposeAsync_OfOneSharingGatewayKeepsTheOtherWorkingAndTheGraphReusable()
    {
        var pool = CreatePool();
        var first = CreateProvider(pool, options => ConfigureCatalog(options, "alpha"));
        await using var second = CreateProvider(pool, options => ConfigureCatalog(options, "beta"));
        var secondGateway = second.GetRequiredService<IMcpGateway>();
        _ = await first.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        _ = await secondGateway.BuildIndexAsync();
        var expected = await ExportGraphAsync(secondGateway);

        await first.DisposeAsync();
        var afterDispose = await ExportGraphAsync(secondGateway);
        await AssertPrimaryMatchesAsync(secondGateway);
        var invocation = await secondGateway.InvokeAsync(
            new McpGatewayInvokeRequest(ToolId: "billing_lookup_invoice", Query: "INV-7"));

        await Assert.That(afterDispose).IsEqualTo(expected);
        await Assert.That((string)invocation.Output!).IsEqualTo("beta:billing_lookup_invoice:INV-7");
        await Assert.That(pool.IdleCount).IsEqualTo(0);

        await second.DisposeAsync();
        await Assert.That(pool.Count).IsEqualTo(1);
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        await using var third = CreateProvider(pool, options => ConfigureCatalog(options, "gamma"));
        var thirdGateway = third.GetRequiredService<IMcpGateway>();
        var thirdBuild = await thirdGateway.BuildIndexAsync();

        await Assert.That(thirdBuild.IsGraphIndexReused).IsTrue();
        await Assert.That(pool.BuildCount).IsEqualTo(1);
        await Assert.That(await ExportGraphAsync(thirdGateway)).IsEqualTo(expected);
        await AssertPrimaryMatchesAsync(thirdGateway);
    }

    [TUnit.Core.Test]
    public async Task DisposeAsync_RetiresReleasedGraphsBeyondTheIdleBound()
    {
        var pool = CreatePool(maximumIdleIndexes: 1);
        await BuildAndDisposeAsync(pool, options => ConfigureCatalog(options, "alpha"));
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        await BuildAndDisposeAsync(pool, options => ConfigureCatalog(options, "alpha", billingAlias: "receipt lookup"));
        await Assert.That(pool.Count).IsEqualTo(1);
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        var newestReused = await BuildAndDisposeAsync(pool, options => ConfigureCatalog(options, "beta", billingAlias: "receipt lookup"));
        var evictedRebuilt = await BuildAndDisposeAsync(pool, options => ConfigureCatalog(options, "beta"));

        await Assert.That(newestReused.IsGraphIndexReused).IsTrue();
        await Assert.That(evictedRebuilt.IsGraphIndexReused).IsFalse();
        await Assert.That(pool.BuildCount).IsEqualTo(3);
        await Assert.That(pool.Count).IsEqualTo(1);
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [TUnit.Core.Test]
    public async Task ReconfigureAsync_RetiresTheSupersededGraphInsteadOfKeepingItIdle()
    {
        var pool = CreatePool();
        await using var provider = CreateProvider(pool, options => ConfigureCatalog(options, "alpha"));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        _ = await gateway.BuildIndexAsync();

        var replacement = new McpGatewayOptions();
        ConfigureCatalog(replacement, "alpha", weatherDescription: "Search marine tide tables and surf swell by harbor.");
        await provider.GetRequiredService<IMcpGatewayCatalogRuntime>().ReconfigureAsync(replacement);
        var search = await gateway.SearchAsync("tide tables for a harbor", 1);
        await WaitUntilAsync(() => pool.Count == 1);

        await Assert.That(search.Matches[0].ToolId).IsEqualTo("weather_search_forecast");
        await Assert.That(search.Matches[0].Description).Contains("tide tables");
        await Assert.That(pool.BuildCount).IsEqualTo(2);
        await Assert.That(pool.IdleCount).IsEqualTo(0);
    }

    private static McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact> CreatePool(int maximumIdleIndexes = 2) =>
        new(maximumIdleIndexes);

    private static ServiceProvider CreateProvider(
        McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact> pool,
        Action<McpGatewayOptions> configure
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(pool);
        services.AddMcpGateway(configure);
        return services.BuildServiceProvider();
    }

    private static async Task<McpGatewayIndexBuildResult> BuildAndDisposeAsync(
        McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact> pool,
        Action<McpGatewayOptions> configure
    )
    {
        await using var provider = CreateProvider(pool, configure);
        return await provider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
    }

    private static void ConfigureCatalog(
        McpGatewayOptions options,
        string marker,
        string weatherDescription = WeatherDescription,
        bool issueLimit = false,
        string billingAlias = "invoice lookup"
    )
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Graph;
        options.MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.SchemaAware;
        options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.Disabled;
        options.DefaultSearchLimit = SearchLimit;
        AddTool(options, CreateQueryTool(marker, "weather_search_forecast", weatherDescription),
            ["forecast lookup"], "weather", readOnly: true);
        AddTool(options, CreateQueryTool(marker, "weather_list_alerts", "List severe weather alerts and warnings for a region."),
            ["storm warnings"], "weather", readOnly: true);
        AddTool(options, CreateQueryTool(marker, "github_search_issues", "Search GitHub issues and pull requests by query."),
            ["issue search"], "source-control", readOnly: true);
        AddTool(options, issueLimit
                ? AIFunctionFactory.Create((string query, int limit) => $"{marker}:github_create_issue:{query}:{limit}",
                    new AIFunctionFactoryOptions { Name = "github_create_issue", Description = "Create a GitHub issue in a repository." })
                : CreateQueryTool(marker, "github_create_issue", "Create a GitHub issue in a repository."),
            ["open issue"], "source-control", readOnly: false);
        AddTool(options, CreateQueryTool(marker, "billing_lookup_invoice", "Look up an invoice by number and return its balance."),
            [billingAlias], "billing", readOnly: true);
        AddTool(options, CreateQueryTool(marker, "billing_refund_invoice", "Refund a paid invoice to the original payment method."),
            ["refund payment"], "billing", readOnly: false, destructive: true);
    }

    private static void AddTool(
        McpGatewayOptions options,
        AIFunction tool,
        string[] aliases,
        string category,
        bool readOnly,
        bool destructive = false
    ) =>
        options.AddTool(
            tool,
            new McpGatewayToolSearchHints(
                Aliases: aliases,
                Categories: [category],
                ReadOnly: readOnly,
                Destructive: destructive,
                Idempotent: readOnly
            ),
            "local"
        );

    private static AIFunction CreateQueryTool(string marker, string name, string description) =>
        AIFunctionFactory.Create(
            (string query) => $"{marker}:{name}:{query}",
            new AIFunctionFactoryOptions { Name = name, Description = description }
        );

    private static async Task<string> ExportGraphAsync(IMcpGateway gateway)
    {
        var export = await ((IMcpGatewayGraphSearch)gateway).ExportMarkdownLdGraphAsync();
        return string.Join(
            Environment.NewLine,
            export.NodeCount.ToString(CultureInfo.InvariantCulture),
            export.EdgeCount.ToString(CultureInfo.InvariantCulture),
            export.JsonLd,
            export.Turtle
        );
    }

    private static async Task AssertPrimaryMatchesAsync(IMcpGateway gateway)
    {
        foreach (var (query, toolId) in PrimaryQueries)
        {
            var result = await gateway.SearchAsync(new McpGatewaySearchRequest(Query: query, MaxResults: SearchLimit));
            await Assert.That(result.RankingMode).IsEqualTo("graph");
            await Assert.That(result.Matches[0].ToolId).IsEqualTo(toolId);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                throw new TimeoutException("Condition was not satisfied within five seconds.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }
}
