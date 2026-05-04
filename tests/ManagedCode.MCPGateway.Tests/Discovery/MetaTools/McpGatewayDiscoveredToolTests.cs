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
                ToolId: "local:incident_status_lookup",
                SourceId: "ops",
                SourceKind: McpGatewaySourceKind.Local,
                ToolName: "incident_status_lookup",
                DisplayName: "Lookup incident status",
                Description: "Inspect incident state by identifier.",
                RequiredArguments: ["incidentId"],
                InputSchemaJson: null,
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
    public async Task CreateDiscoveredTools_PrefixesNamesThatStartWithDigits()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var discoveredTools = toolSet.CreateDiscoveredTools([
            new McpGatewaySearchMatch(
                ToolId: "local:123-tool",
                SourceId: "9-source",
                SourceKind: McpGatewaySourceKind.Local,
                ToolName: "123 tool",
                DisplayName: null,
                Description: "Example tool.",
                RequiredArguments: [],
                InputSchemaJson: null,
                Score: 0.9d
            ),
        ]);

        await Assert.That(discoveredTools.Count).IsEqualTo(1);
        await Assert.That(discoveredTools[0].Name).IsEqualTo("t_123_tool");
    }
}
