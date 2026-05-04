using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_DefaultGraphStrategyUsesSchemaAwareGraphSearchAndRespectsLimit()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureDefaultMarkdownLdSearchTools
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("search");

        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert.That(searchResult.UsedSchemaSearch).IsTrue();
        await Assert.That(searchResult.UsedSchemaFallback).IsFalse();
        await Assert.That(searchResult.Matches.Count > 0).IsTrue();
        await Assert.That(searchResult.Matches.Count <= 5).IsTrue();
        await Assert.That(searchResult.FocusedGraphNodeCount).IsGreaterThan(0);
        await Assert.That(searchResult.FocusedGraphEdgeCount).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task SearchGraphAsync_ReturnsSchemaSparqlAndMappedGatewayTools()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_issues",
                    "Search GitHub issues and pull requests by user query."
                )
            );
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

        await gateway.BuildIndexAsync();
        var searchResult = await graphSearch.SearchGraphAsync(
            new McpGatewayGraphSearchRequest("severity filter") { MaxResults = 3 }
        );

        await Assert.That(searchResult.IsFederated).IsFalse();
        await Assert.That(searchResult.GeneratedSparql).Contains("SELECT");
        await Assert.That(searchResult.Matches.Count).IsGreaterThan(0);
        await Assert
            .That(searchResult.Matches[0].ToolMatch?.ToolId)
            .IsEqualTo("local:advisory_lookup");
        await Assert.That(searchResult.Matches[0].Evidence.Count).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task DescribeGraphSchemaAsync_ReturnsValidatedSchemaProfileAndIndexState()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

        var descriptors = await gateway.ListToolsAsync();
        var expectedExport = await McpGatewayMarkdownLdGraphFile.ExportAsync(descriptors);
        var buildResult = await gateway.BuildIndexAsync();
        var schema = await graphSearch.DescribeGraphSchemaAsync();

        await Assert.That(schema.IsGraphAvailable).IsTrue();
        await Assert.That(schema.GraphSearchMode).IsEqualTo(McpGatewayMarkdownLdGraphSearchMode.Hybrid);
        await Assert.That(schema.Prefixes.ContainsKey("schema")).IsTrue();
        await Assert
            .That(schema.TextPredicates.Any(static predicate => predicate.PredicateId == "schema:name"))
            .IsTrue();
        await Assert
            .That(
                schema.RelationshipPredicates.Any(static predicate =>
                    predicate.PredicateId == "schema:about"
                )
            )
            .IsTrue();
        await Assert.That(schema.GraphNodeCount).IsEqualTo(expectedExport.NodeCount);
        await Assert.That(schema.GraphEdgeCount).IsEqualTo(expectedExport.EdgeCount);
        await Assert.That(buildResult.GraphNodeCount).IsEqualTo(expectedExport.NodeCount);
        await Assert.That(buildResult.GraphEdgeCount).IsEqualTo(expectedExport.EdgeCount);
    }

    [TUnit.Core.Test]
    public async Task SearchGraphAsync_ReturnsMcpGraphToolEvidenceAndMappedTools()
    {
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

        await gateway.BuildIndexAsync();
        var searchResult = await graphSearch.SearchGraphAsync(
            new McpGatewayGraphSearchRequest("search story feed items by query text")
            {
                MaxResults = 4,
            }
        );

        await Assert.That(searchResult.GeneratedSparql).Contains("SELECT");
        await Assert.That(searchResult.GeneratedExpansionSparql).Contains("SELECT");
        var storySearchMatch = searchResult.Matches.FirstOrDefault(static match =>
            match.ToolMatch?.ToolId == "graph-mcp:story_item_search"
        );

        await Assert.That(storySearchMatch).IsNotNull();
        await Assert
            .That(
                searchResult.Matches.Any(static match =>
                    match.ToolMatch?.ToolId == "graph-mcp:story_item_detail"
                )
            )
            .IsTrue();
        await Assert.That(storySearchMatch!.Evidence.Count).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    [TUnit.Core.NotInParallel]
    public async Task SearchGraphAsync_FederatesThroughExplicitLocalGraphService()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

        var searchResult = await graphSearch.SearchGraphAsync(
            new McpGatewayGraphSearchRequest("severity filter")
            {
                MaxResults = 3,
                UseFederation = true,
                IncludeLocalGatewayGraph = true,
            }
        );

        await Assert.That(searchResult.IsFederated).IsTrue();
        await Assert.That(searchResult.GeneratedSparql).Contains("SERVICE");
        await Assert
            .That(searchResult.Matches.Any(static match =>
                match.ToolMatch?.ToolId == "local:advisory_lookup"
            ))
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchGraphAsync_BlocksUnconfiguredFederatedEndpoint()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

        var searchResult = await graphSearch.SearchGraphAsync(
            new McpGatewayGraphSearchRequest("severity filter")
            {
                UseFederation = true,
                IncludeLocalGatewayGraph = false,
                ServiceEndpoints = ["https://knowledge.example.com/sparql"],
            }
        );

        await Assert.That(searchResult.IsFederated).IsTrue();
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "graph_federation_endpoint_blocked"
                )
            )
            .IsTrue();
        await Assert.That(searchResult.ServiceEndpointSpecifiers).IsEmpty();
    }

    [TUnit.Core.Test]
    public async Task McpGatewayMarkdownLdGraphFile_ExportsPortableGraphArtifacts()
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

        var export = await McpGatewayMarkdownLdGraphFile.ExportAsync([descriptor]);

        await Assert.That(export.NodeCount).IsGreaterThan(0);
        await Assert.That(export.EdgeCount).IsGreaterThan(0);
        await Assert.That(export.JsonLd).Contains("story_item_search");
        await Assert.That(export.Turtle).Contains("story_item_search");
        await Assert.That(export.MermaidFlowchart).Contains("story_item_search");
        await Assert.That(export.DotGraph).Contains("story_item_search");
    }
}
