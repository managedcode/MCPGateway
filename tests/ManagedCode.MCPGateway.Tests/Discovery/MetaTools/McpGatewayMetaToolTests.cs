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
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            options =>
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
                        SearchWeather,
                        "weather_search_forecast",
                        "Search weather forecast and temperature information by city name."
                    )
                );
            },
            embeddingGenerator
        );

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var searchTool = GetFunction(
            gateway.CreateMetaTools(),
            McpGatewayToolSet.DefaultSearchToolName
        );

        var result = await searchTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "search",
                    ["contextSummary"] = "weather forecast",
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();

        var searchResult = (JsonElement)result!;
        var matches = GetJsonProperty(searchResult, "matches");
        await Assert.That(matches[0].ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert
            .That(GetJsonProperty(matches[0], "toolId").GetString())
            .IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task CreateMetaTools_InvokeToolSupportsContextSummary()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    EchoContextSummary,
                    "context_summary_echo",
                    "Echo query and context summary."
                )
            );
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeTool = GetFunction(
            gateway.CreateMetaTools(),
            McpGatewayToolSet.DefaultInvokeToolName
        );
        var result = await invokeTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["toolId"] = "local:context_summary_echo",
                    ["query"] = "open github",
                    ["contextSummary"] = "user is on repository settings page",
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();

        var invokeResult = (JsonElement)result!;
        await Assert.That(GetJsonProperty(invokeResult, "isSuccess").GetBoolean()).IsTrue();
        await Assert
            .That(GetJsonProperty(invokeResult, "output").GetString())
            .IsEqualTo("open github|user is on repository settings page");
    }

    [TUnit.Core.Test]
    public async Task CreateMetaTools_RouteToolGroupsToolsByCategoryAndPrefersReadOnlyMatches()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchIncidents,
                    "incident_status_lookup",
                    "Inspect incident state by identifier."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["operations"],
                    Tags: ["incident", "status"],
                    ReadOnly: true,
                    Idempotent: true,
                    CostTier: McpGatewayToolCostTier.Low,
                    LatencyTier: McpGatewayToolLatencyTier.Low
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    AcknowledgeIncident,
                    "incident_acknowledge",
                    "Acknowledge an incident."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["operations"],
                    Tags: ["incident", "acknowledge"],
                    ReadOnly: false,
                    Idempotent: true,
                    CostTier: McpGatewayToolCostTier.Medium,
                    LatencyTier: McpGatewayToolLatencyTier.Medium
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchDeployments,
                    "deployment_status_lookup",
                    "Inspect deployment status by environment."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["deployments"],
                    Tags: ["deployment", "status"],
                    ReadOnly: true,
                    Idempotent: true,
                    CostTier: McpGatewayToolCostTier.Low,
                    LatencyTier: McpGatewayToolLatencyTier.Low
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var routeTool = GetFunction(
            gateway.CreateMetaTools(),
            McpGatewayToolSet.DefaultRouteToolName
        );
        var result = await routeTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "lookup incident status",
                    ["maxCategories"] = 2,
                    ["maxToolsPerCategory"] = 2,
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();
        var routeResult = (JsonElement)result!;
        var categories = GetJsonProperty(routeResult, "categories");
        await Assert.That(categories[0].ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert
            .That(GetJsonProperty(categories[0], "category").GetString())
            .IsEqualTo("operations");

        var operationsTools = GetJsonProperty(categories[0], "tools");
        await Assert
            .That(GetJsonProperty(operationsTools[0], "toolId").GetString())
            .IsEqualTo("local:incident_status_lookup");

        var suggestedMatches = GetJsonProperty(routeResult, "suggestedMatches");
        await Assert
            .That(GetJsonProperty(suggestedMatches[0], "toolId").GetString())
            .IsEqualTo("local:incident_status_lookup");
    }

    [TUnit.Core.Test]
    public async Task CreateMetaTools_RouteToolCanSurfaceDisabledActionToolsWhenRequested()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchIncidents,
                    "incident_status_lookup",
                    "Inspect incident state by identifier."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["operations"],
                    Tags: ["incident", "status"],
                    ReadOnly: true,
                    Idempotent: true,
                    CostTier: McpGatewayToolCostTier.Low,
                    LatencyTier: McpGatewayToolLatencyTier.Low
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    CreateIncidentBridge,
                    "incident_create_bridge",
                    "Create a coordination bridge and failover plan for an active incident."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["operations"],
                    Tags: ["incident", "bridge", "failover", "war-room"],
                    UsageExamples:
                    [
                        new McpGatewayToolExample(
                            "open a bridge for incident 42",
                            "{\"bridgeId\":\"bridge-42\"}",
                            "Create a war-room bridge for an active incident."
                        ),
                        new McpGatewayToolExample(
                            "fail over checkout-api for incident 42",
                            "{\"plan\":\"prepared\"}",
                            "Prepare the incident failover action."
                        ),
                    ],
                    ReadOnly: false,
                    Idempotent: true,
                    Destructive: true,
                    CostTier: McpGatewayToolCostTier.Medium,
                    LatencyTier: McpGatewayToolLatencyTier.Medium,
                    EnabledByDefault: false
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    SearchRunbooks,
                    "incident_runbook_search",
                    "Inspect incident recovery runbooks by service name."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["docs"],
                    Tags: ["runbook", "recovery"],
                    ReadOnly: true,
                    Idempotent: true
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var routeTool = GetFunction(
            gateway.CreateMetaTools(),
            McpGatewayToolSet.DefaultRouteToolName
        );
        var result = await routeTool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = "open a bridge and fail over checkout-api for incident 42",
                    ["contextSummary"] =
                        "incident already confirmed and the operator now needs an action tool",
                    ["preferReadOnly"] = false,
                    ["includeDisabledTools"] = true,
                    ["maxCategories"] = 2,
                    ["maxToolsPerCategory"] = 2,
                },
                StringComparer.OrdinalIgnoreCase
            )
        );

        await Assert.That(result).IsTypeOf<JsonElement>();
        var routeResult = (JsonElement)result!;
        var categories = GetJsonProperty(routeResult, "categories");
        await Assert
            .That(GetJsonProperty(categories[0], "category").GetString())
            .IsEqualTo("operations");

        var operationsTools = GetJsonProperty(categories[0], "tools");
        await Assert
            .That(GetJsonProperty(operationsTools[0], "toolId").GetString())
            .IsEqualTo("local:incident_create_bridge");
        await Assert
            .That(GetJsonProperty(operationsTools[0], "isEnabledByDefault").GetBoolean())
            .IsFalse();

        var suggestedMatches = GetJsonProperty(routeResult, "suggestedMatches");
        await Assert
            .That(GetJsonProperty(suggestedMatches[0], "toolId").GetString())
            .IsEqualTo("local:incident_create_bridge");
    }

    private static AIFunction GetFunction(IReadOnlyList<AITool> tools, string toolName) =>
        (tools.Single(tool => tool.Name == toolName) as AIFunction)
        ?? throw new InvalidOperationException($"Tool '{toolName}' is not an AIFunction.");

    private static string SearchGitHub([Description("Search query text.")] string query) =>
        $"github:{query}";

    private static string SearchWeather(
        [Description("City or weather request text.")] string query
    ) => $"weather:{query}";

    private static string EchoContextSummary(
        [Description("Main query text.")] string query,
        [Description("Execution context summary.")] string contextSummary
    ) => $"{query}|{contextSummary}";

    private static string SearchIncidents([Description("Incident lookup text.")] string query) =>
        $"incident-status:{query}";

    private static string AcknowledgeIncident(
        [Description("Incident identifier.")] string incidentId
    ) => $"incident-ack:{incidentId}";

    private static string SearchDeployments(
        [Description("Deployment lookup text.")] string query
    ) => $"deployment-status:{query}";

    private static string CreateIncidentBridge(
        [Description("Incident action request text.")] string query
    ) => $"incident-bridge:{query}";

    private static string SearchRunbooks([Description("Runbook lookup text.")] string query) =>
        $"runbook-search:{query}";

    private static JsonElement GetJsonProperty(JsonElement element, string name) =>
        element
            .EnumerateObject()
            .First(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
            )
            .Value;
}
