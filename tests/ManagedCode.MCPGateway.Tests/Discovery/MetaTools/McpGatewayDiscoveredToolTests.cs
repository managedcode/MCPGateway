using System.ComponentModel;
using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayDiscoveredToolTests
{
    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_DescriptionIncludesExecutionMetadataAndExamples()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools([
            new McpGatewaySearchMatch(
                ToolId: "incident_status_lookup",
                SourceId: "ops",
                SourceKind: McpGatewaySourceKind.Local,
                ToolName: "incident_status_lookup",
                DisplayName: "Lookup incident status",
                Description: "Inspect incident state by identifier.",
                RequiredArguments: ["incidentId"],
                InputSchema: null,
                Score: 0.95d
            )
            {
                Categories = ["operations"],
                Tags = ["incident", "status"],
                DataSources = ["ops-api"],
                UsageExamples = [new McpGatewayToolExample("incident 42 status")],
                IsReadOnly = true,
                IsIdempotent = true,
                CostTier = McpGatewayToolCostTier.Low,
                LatencyTier = McpGatewayToolLatencyTier.Low,
            },
        ]);

        await Assert.That(discoveredTools.Count).IsEqualTo(1);
        await Assert.That(discoveredTools[0].Description).Contains("Categories: operations.");
        await Assert
            .That(discoveredTools[0].Description)
            .Contains("Execution hints: read-only, idempotent, cost low, latency low.");
        await Assert
            .That(discoveredTools[0].Description)
            .Contains("Example input: incident 42 status.");
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_AllowsNamesThatStartWithDigits()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools([
            new McpGatewaySearchMatch(
                ToolId: "123-tool",
                SourceId: "9-source",
                SourceKind: McpGatewaySourceKind.Local,
                ToolName: "123 tool",
                DisplayName: null,
                Description: "Example tool.",
                RequiredArguments: [],
                InputSchema: null,
                Score: 0.9d
            ),
        ]);

        await Assert.That(discoveredTools.Count).IsEqualTo(1);
        await Assert.That(discoveredTools[0].Name).IsEqualTo("123_tool");
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_DoesNotHashShortUniqueNames()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools([
            CreateSearchMatch("notion", "notion-fetch", "notion-fetch"),
        ]);

        await Assert.That(discoveredTools.Count).IsEqualTo(1);
        await Assert.That(discoveredTools[0].Name).IsEqualTo("notion-fetch");
        await Assert.That(HasHashSuffix(discoveredTools[0].Name)).IsFalse();
        await Assert.That(IsMcpSafeToolName(discoveredTools[0].Name)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_UsesSourceQualifiedNameBeforeHashForShortCollisions()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools(
            [
                CreateSearchMatch("docs", "search", "docs_search"),
                CreateSearchMatch("ops", "search", "ops_search"),
            ]
        );
        var toolNames = discoveredTools.Select(static tool => tool.Name).ToArray();

        await Assert.That(toolNames).IsEquivalentTo(["search", "ops_search"]);
        await Assert.That(toolNames.All(IsMcpSafeToolName)).IsTrue();
        await Assert.That(toolNames.All(static name => !HasHashSuffix(name))).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_TreatsReservedNamesCaseInsensitivelyAfterNormalization()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools(
            [CreateSearchMatch("notion", "Notion Fetch", "notion_fetch")],
            reservedToolNames: ["NOTION_FETCH"]
        );

        await Assert.That(discoveredTools.Count).IsEqualTo(1);
        await Assert.That(discoveredTools[0].Name).IsEqualTo("notion_fetch_2");
        await Assert.That(IsMcpSafeToolName(discoveredTools[0].Name)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_UsesHashShortenedSafeNamesForLongUnsafeMatches()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var firstToolName = string.Concat(
            "Notion Fetch Page Details From Customer Workspace / Shared Database ",
            new string('A', 80),
            " First"
        );
        var secondToolName = string.Concat(
            "Notion Fetch Page Details From Customer Workspace / Shared Database ",
            new string('A', 80),
            " Second"
        );

        var discoveredTools = toolSet.CreateDiscoveredTools(
            [
                CreateSearchMatch("notion-source-one", firstToolName, "first_tool"),
                CreateSearchMatch("notion-source-two", secondToolName, "second_tool"),
            ]
        );
        var toolNames = discoveredTools.Select(static tool => tool.Name).ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(2);
        await Assert.That(toolNames.All(IsMcpSafeToolName)).IsTrue();
        await Assert.That(toolNames.All(HasHashSuffix)).IsTrue();
        await Assert.That(toolNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_KeepsCollisionSuffixesMcpSafeForLongNames()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var toolName = string.Concat(
            "Notion Fetch Page Details From Customer Workspace / Shared Database ",
            new string('B', 90)
        );

        var discoveredTools = toolSet.CreateDiscoveredTools(
            [
                CreateSearchMatch("notion-remote-workspace-with-long-name", toolName, "first_tool"),
                CreateSearchMatch("notion-remote-workspace-with-long-name", toolName, "second_tool"),
                CreateSearchMatch("notion-remote-workspace-with-long-name", toolName, "third_tool"),
            ]
        );
        var toolNames = discoveredTools.Select(static tool => tool.Name).ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(3);
        await Assert.That(toolNames.All(IsMcpSafeToolName)).IsTrue();
        await Assert.That(toolNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(3);
        await Assert.That(toolNames[2]).EndsWith("_2");
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_PreservesGatewayToolIdMetadataWhenPublicNameChanges()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var toolName = string.Concat(
            "Notion Fetch Page Details From Customer Workspace / Shared Database ",
            new string('D', 90)
        );

        var discoveredTools = toolSet.CreateDiscoveredTools([
            CreateSearchMatch("notion-source", toolName, "gateway_tool_id"),
        ]);
        var function =
            discoveredTools.Single() as AIFunction
            ?? throw new InvalidOperationException("Discovered tool is not an AIFunction.");

        await Assert.That(function.Name).IsNotEqualTo("gateway_tool_id");
        await Assert.That(HasHashSuffix(function.Name)).IsTrue();
        await Assert
            .That(function.AdditionalProperties[McpGatewayToolSet.DiscoveredToolIdPropertyName])
            .IsEqualTo("gateway_tool_id");
        await Assert
            .That(function.AdditionalProperties[McpGatewayToolSet.DiscoveredToolSourceIdPropertyName])
            .IsEqualTo("notion-source");
        await Assert.That(IsMcpSafeToolName(function.Name)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateDiscoveredTools_InvokesUnderlyingToolWhenProxyNameWasHashShortened()
    {
        var rawToolName = string.Concat(
            "notion.fetch.page.details.from.customer.workspace.shared.database.",
            new string('C', 60)
        );
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "notion-remote-workspace",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    rawToolName,
                    "Fetch a Notion page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var descriptor = (await gateway.ListToolsAsync()).Single();
        var discoveredTools = toolSet.CreateDiscoveredTools([CreateSearchMatch(descriptor)]);
        var proxyTool = GetSingleFunction(discoveredTools);

        await Assert.That(descriptor.ToolName).IsEqualTo(rawToolName);
        await Assert.That(IsMcpSafeToolName(descriptor.ToolId)).IsTrue();
        await Assert.That(HasHashSuffix(descriptor.ToolId)).IsTrue();
        await Assert.That(proxyTool.Name).IsEqualTo(descriptor.ToolId);

        var result = await proxyTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["arguments"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["pageId"] = "page-42",
                    },
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();
        var invokeResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(invokeResult, "isSuccess").GetBoolean()).IsTrue();
        await Assert.That(GetJsonProperty(invokeResult, "toolId").GetString()).IsEqualTo(descriptor.ToolId);
        await Assert.That(GetJsonProperty(invokeResult, "output").GetString()).IsEqualTo("page:page-42");
    }

    private static McpGatewaySearchMatch CreateSearchMatch(
        string sourceId,
        string toolName,
        string toolId
    ) =>
        new(
            ToolId: toolId,
            SourceId: sourceId,
            SourceKind: McpGatewaySourceKind.Local,
            ToolName: toolName,
            DisplayName: null,
            Description: "Example tool.",
            RequiredArguments: [],
            InputSchema: null,
            Score: 0.9d
        );

    private static McpGatewaySearchMatch CreateSearchMatch(McpGatewayToolDescriptor descriptor) =>
        new(
            ToolId: descriptor.ToolId,
            SourceId: descriptor.SourceId,
            SourceKind: descriptor.SourceKind,
            ToolName: descriptor.ToolName,
            DisplayName: descriptor.DisplayName,
            Description: descriptor.Description,
            RequiredArguments: descriptor.RequiredArguments,
            InputSchema: descriptor.InputSchema,
            Score: 1d
        );

    private static AIFunction GetSingleFunction(IReadOnlyList<AITool> tools) =>
        (tools.Single() as AIFunction)
        ?? throw new InvalidOperationException("Discovered tool is not an AIFunction.");

    private static JsonElement GetJsonProperty(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
            ? property
            : throw new InvalidOperationException($"Missing JSON property '{propertyName}'.");

    private static bool IsMcpSafeToolName(string toolName) =>
        toolName.Length is > 0 and <= McpGatewayProtocolName.MaxNameLength
        && toolName.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character is '_' or '-'
        );

    private static bool HasHashSuffix(string toolName)
    {
        if (toolName.Length < 9 || toolName[^9] != '_')
        {
            return false;
        }

        return toolName[^8..].All(static character =>
            character is >= 'a' and <= 'f' || character is >= '0' and <= '9'
        );
    }

    private static string FetchNotionPage([Description("Page identifier.")] string pageId) =>
        $"page:{pageId}";
}
