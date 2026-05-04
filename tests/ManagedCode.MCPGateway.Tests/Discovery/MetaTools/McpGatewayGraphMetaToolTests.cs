using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGraphMetaToolTests
{
    [TUnit.Core.Test]
    public async Task CreateGraphTools_SchemaToolReturnsSchemaProfileAndGraphState()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var graphSchemaTool = GetFunction(
            toolSet.CreateGraphTools(),
            McpGatewayToolSet.DefaultGraphSchemaToolName
        );

        var result = await graphSchemaTool.InvokeAsync(new AIFunctionArguments());

        await Assert.That(result).IsTypeOf<JsonElement>();
        var schemaResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(schemaResult, "isGraphAvailable").GetBoolean()).IsTrue();
        await Assert
            .That(GetJsonProperty(schemaResult, "graphSearchMode").GetString())
            .IsEqualTo(McpGatewayMarkdownLdGraphSearchMode.Hybrid.ToString());
        await Assert.That(GetJsonProperty(schemaResult, "graphNodeCount").GetInt32()).IsGreaterThan(0);

        var prefixes = GetJsonProperty(schemaResult, "prefixes");
        await Assert.That(GetJsonProperty(prefixes, "schema").GetString()).IsEqualTo("https://schema.org/");

        var textPredicates = GetJsonProperty(schemaResult, "textPredicates");
        await Assert
            .That(
                textPredicates.EnumerateArray().Any(static predicate =>
                    GetJsonProperty(predicate, "predicateId").GetString() == "schema:name"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateGraphTools_IndexBuildToolBuildsToolGraphIndex()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var indexBuildTool = GetFunction(
            toolSet.CreateGraphTools(),
            McpGatewayToolSet.DefaultToolIndexBuildToolName
        );

        var result = await indexBuildTool.InvokeAsync(new AIFunctionArguments());

        await Assert.That(result).IsTypeOf<JsonElement>();
        var indexResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(indexResult, "toolCount").GetInt32()).IsEqualTo(1);
        await Assert
            .That(GetJsonProperty(indexResult, "isGraphSearchEnabled").GetBoolean())
            .IsTrue();
        await Assert.That(GetJsonProperty(indexResult, "graphNodeCount").GetInt32()).IsGreaterThan(0);
        await Assert.That(GetJsonProperty(indexResult, "graphEdgeCount").GetInt32()).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task CreateGraphTools_SearchToolReturnsGeneratedSparqlAndMappedTool()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "advisory_lookup",
                    "Lookup advisory records by severity filter."
                )
            );
        });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var graphSearchTool = GetFunction(
            toolSet.CreateGraphTools(),
            McpGatewayToolSet.DefaultGraphSearchToolName
        );

        var result = await graphSearchTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "severity filter",
                    ["maxResults"] = 3,
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();
        var graphResult = (JsonElement)result!;
        await Assert
            .That(GetJsonProperty(graphResult, "generatedSparql").GetString())
            .Contains("SELECT");
        var matches = GetJsonProperty(graphResult, "matches");
        await Assert
            .That(GetJsonProperty(GetJsonProperty(matches[0], "toolMatch"), "toolId").GetString())
            .IsEqualTo("local:advisory_lookup");
    }

    [TUnit.Core.Test]
    [TUnit.Core.NotInParallel]
    public async Task CreateGraphTools_FederatedSearchToolReturnsMcpGraphSparqlAndMappedTools()
    {
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var federatedGraphSearchTool = GetFunction(
            toolSet.CreateGraphTools(),
            McpGatewayToolSet.DefaultGraphFederatedSearchToolName
        );

        var result = await federatedGraphSearchTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "search story feed items by query text",
                    ["maxResults"] = 4,
                    ["includeLocalGatewayGraph"] = true,
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();
        var graphResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(graphResult, "isFederated").GetBoolean()).IsTrue();
        await Assert
            .That(GetJsonProperty(graphResult, "generatedSparql").GetString())
            .Contains("SERVICE");

        var matches = GetJsonProperty(graphResult, "matches");
        await Assert
            .That(
                matches.EnumerateArray().Any(static match =>
                    GetJsonProperty(GetJsonProperty(match, "toolMatch"), "toolId").GetString()
                    == "graph-mcp:story_item_search"
                )
            )
            .IsTrue();

        await Assert
            .That(
                matches.EnumerateArray().Any(static match =>
                    GetJsonProperty(GetJsonProperty(match, "toolMatch"), "toolId").GetString()
                    == "graph-mcp:story_item_detail"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateGraphTools_ExportToolReturnsPortableMcpGraphArtifacts()
    {
        await using var serverHost = await TestMcpServerHost.StartGraphAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("graph-mcp", serverHost.Client, disposeClient: false);
        });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var exportGraphTool = GetFunction(
            toolSet.CreateGraphTools(),
            McpGatewayToolSet.DefaultGraphExportToolName
        );

        var result = await exportGraphTool.InvokeAsync(new AIFunctionArguments());

        await Assert.That(result).IsTypeOf<JsonElement>();
        var exportResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(exportResult, "nodeCount").GetInt32()).IsGreaterThan(0);
        await Assert.That(GetJsonProperty(exportResult, "edgeCount").GetInt32()).IsGreaterThan(0);
        await Assert
            .That(GetJsonProperty(exportResult, "jsonLd").GetString())
            .Contains("story_item_search");
        await Assert
            .That(GetJsonProperty(exportResult, "turtle").GetString())
            .Contains("story_item_search");
        await Assert
            .That(GetJsonProperty(exportResult, "mermaidFlowchart").GetString())
            .Contains("story_item_search");
        await Assert
            .That(GetJsonProperty(exportResult, "dotGraph").GetString())
            .Contains("story_item_search");
    }

    private static AIFunction GetFunction(IReadOnlyList<AITool> tools, string toolName) =>
        (tools.Single(tool => tool.Name == toolName) as AIFunction)
        ?? throw new InvalidOperationException($"Tool '{toolName}' is not an AIFunction.");

    private static string SearchGitHub([Description("Search query text.")] string query) =>
        $"github:{query}";

    private static JsonElement GetJsonProperty(JsonElement element, string name) =>
        element
            .EnumerateObject()
            .First(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
            )
            .Value;
}
