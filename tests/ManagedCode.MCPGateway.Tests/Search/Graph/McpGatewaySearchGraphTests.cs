using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_BuildsMarkdownLdGraphForToolDescriptors()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("temperature forecast by city", maxResults: 1);

        await Assert.That(buildResult.ToolCount).IsEqualTo(2);
        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(buildResult.GraphNodeCount).IsGreaterThan(2);
        await Assert.That(buildResult.GraphEdgeCount).IsGreaterThan(0);
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "graph_fallback"
                )
            )
            .IsFalse();
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_GraphStrategyRanksMcpToolDescriptors()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            "github repository search query",
            maxResults: 1
        );

        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "graph_fallback"
                )
            )
            .IsFalse();
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("test-mcp:github_repository_search");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_GraphStrategyReturnsFocusedMcpRelatedAndNextStepMatches()
    {
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            "search story feed items by query text",
            maxResults: 1
        );

        await Assert.That(buildResult.ToolCount).IsEqualTo(4);
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("graph-mcp:story_item_search");
        await Assert.That(searchResult.RelatedMatches.Count).IsGreaterThan(0);
        await Assert
            .That(
                searchResult.NextStepMatches.Any(static match =>
                    match.ToolId == "graph-mcp:story_item_detail"
                )
            )
            .IsTrue();
        await Assert
            .That(
                searchResult.RelatedMatches.Any(static match =>
                    match.ToolId == "graph-mcp:people_profile_search"
                )
            )
            .IsFalse();
        await Assert
            .That(
                searchResult.NextStepMatches.Any(static match =>
                    match.ToolId == "graph-mcp:people_profile_search"
                )
            )
            .IsFalse();
        await Assert.That(searchResult.FocusedGraphNodeCount).IsGreaterThan(0);
        await Assert.That(searchResult.FocusedGraphEdgeCount).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_FileSystemMarkdownLdGraphModeUsesGeneratedBundleFile()
    {
        var graphFile = CreateTemporaryGraphFilePath();
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();

        await using (
            var authoringProvider = GatewayTestServiceProviderFactory.Create(options =>
            {
                options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
            })
        )
        {
            var authoringGateway = authoringProvider.GetRequiredService<IMcpGateway>();
            var descriptors = await authoringGateway.ListToolsAsync();
            var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors);
            await McpGatewayMarkdownLdGraphFile.WriteAsync(graphFile, documents);
        }

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            options.UseMarkdownLdGraphFile(graphFile);
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            "search story feed items by query text",
            maxResults: 1
        );

        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("graph-mcp:story_item_search");
        await Assert
            .That(
                searchResult.NextStepMatches.Any(static match =>
                    match.ToolId == "graph-mcp:story_item_detail"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_FileSystemMarkdownLdGraphModeUsesGeneratedDirectory()
    {
        var graphDirectory = CreateTemporaryDirectoryPath();
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();

        await using (
            var authoringProvider = GatewayTestServiceProviderFactory.Create(options =>
            {
                options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
            })
        )
        {
            var authoringGateway = authoringProvider.GetRequiredService<IMcpGateway>();
            var descriptors = await authoringGateway.ListToolsAsync();
            foreach (var document in McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors))
            {
                var documentPath = Path.Combine(graphDirectory, document.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
                await File.WriteAllTextAsync(documentPath, document.Content);
            }
        }

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            options.UseMarkdownLdGraphFile(graphDirectory);
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("story comments list", maxResults: 1);

        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("graph-mcp:story_comments_list");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_FileSystemMarkdownLdGraphModeReportsMissingPath()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            options.MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.FileSystem;
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeather,
                    "weather_search_forecast",
                    "Search weather forecast and temperature information by city name."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("weather forecast", maxResults: 1);

        await Assert.That(buildResult.IsGraphSearchEnabled).IsFalse();
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "markdown_ld_graph_path_missing"
                )
            )
            .IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.Matches.Count).IsEqualTo(0);
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "graph_unavailable"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task McpGatewayMarkdownLdGraphFile_CreatesRoundTrippableGraphBundle()
    {
        var descriptor = new McpGatewayToolDescriptor(
            "local:story_item_search",
            "local",
            McpGatewaySourceKind.Local,
            "story_item_search",
            "Story search",
            "Search story feed items by query text.",
            ["query"],
            null
        );
        var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments([descriptor]);
        var graphFile = CreateTemporaryGraphFilePath();

        await McpGatewayMarkdownLdGraphFile.WriteAsync(graphFile, documents);
        var roundTrippedDocuments = await McpGatewayMarkdownLdGraphFile.ReadAsync(graphFile);

        await Assert.That(roundTrippedDocuments.Count).IsEqualTo(1);
        await Assert
            .That(roundTrippedDocuments[0].Path)
            .IsEqualTo("tools/local/story_item_search.md");
        await Assert
            .That(roundTrippedDocuments[0].CanonicalUri)
            .IsEqualTo(
                "https://managedcode.com/mcpgateway/knowledge/tools/local/story_item_search/"
            );
        await Assert.That(roundTrippedDocuments[0].Content).Contains("graph_groups");
    }

    private static string CreateTemporaryGraphFilePath()
    {
        var directory = CreateTemporaryDirectoryPath();
        return Path.Combine(directory, "mcp-tools.graph.json");
    }

    private static string CreateTemporaryDirectoryPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "ManagedCode.MCPGateway.Tests",
            Guid.NewGuid().ToString("N")
        );
}
