using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerAdvancedIntegrationTests
{
    private static readonly string[] OperationsCategory = ["operations"];
    private static readonly string[] IncidentTags = ["incident", "status"];
    private static readonly string[] OpsDataSources = ["ops-api"];
    private static readonly string[] RecoveryCategories = ["operations", "recovery"];
    private static readonly string[] RecoveryTags =
    [
        "incident",
        "mitigation",
        "failover",
        "war-room",
    ];
    private static readonly string[] RecoveryDataSources = ["incident-automation", "runbook-api"];

    [Test]
    public async Task ListToolsAsync_ExportsToolAnnotationsAndMetadataToSdkClients()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddTool(
                "local-ops",
                TestFunctionFactory.CreateFunction(
                    LookupIncidentStatus,
                    "incident_status_lookup",
                    "Inspect incident state by identifier.",
                    new Dictionary<string, object?>
                    {
                        ["DisplayName"] = "Lookup incident status",
                        ["categories"] = OperationsCategory,
                        ["tags"] = IncidentTags,
                        ["dataSources"] = OpsDataSources,
                        ["usageExamples"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["input"] = "incident 42 status",
                                ["output"] = "{\"status\":\"open\"}",
                                ["description"] = "Check whether an incident is still active.",
                            },
                            new Dictionary<string, object?>
                            {
                                ["input"] = "is incident 42 still blocking checkout?",
                                ["output"] =
                                    "{\"status\":\"investigating\",\"service\":\"checkout\"}",
                                ["description"] =
                                    "Inspect whether a production incident is still active.",
                            },
                        },
                        ["readOnly"] = true,
                        ["idempotent"] = true,
                        ["openWorld"] = true,
                        ["costTier"] = "Low",
                        ["latencyTier"] = "Low",
                        ["enabledByDefault"] = false,
                    }
                )
            );
        });

        var tools = await gatewayServer.Client.ListToolsAsync();
        var tool = tools.Single(static candidate =>
            candidate.Name == "local-ops:incident_status_lookup"
        );

        await Assert.That(tool.ProtocolTool?.Annotations?.ReadOnlyHint).IsTrue();
        await Assert.That(tool.ProtocolTool?.Annotations?.IdempotentHint).IsTrue();
        await Assert.That(tool.ProtocolTool?.Annotations?.OpenWorldHint).IsTrue();
        await Assert
            .That(tool.ProtocolTool?.Annotations?.Title)
            .IsEqualTo("Lookup incident status");
        await Assert.That(tool.ProtocolTool?.Meta).IsTypeOf<JsonObject>();

        var meta = (JsonObject)tool.ProtocolTool!.Meta!;
        await Assert.That(meta["enabledByDefault"]!.GetValue<bool>()).IsFalse();
        await Assert.That(meta["costTier"]!.GetValue<string>()).IsEqualTo("Low");
        await Assert.That(meta["latencyTier"]!.GetValue<string>()).IsEqualTo("Low");
        await Assert.That(meta["categories"]).IsTypeOf<JsonArray>();
        await Assert.That(meta["tags"]).IsTypeOf<JsonArray>();
        await Assert.That(meta["dataSources"]).IsTypeOf<JsonArray>();
        await Assert
            .That(((JsonArray)meta["categories"]!)[0]!.GetValue<string>())
            .IsEqualTo("operations");
        await Assert.That(((JsonArray)meta["tags"]!)[0]!.GetValue<string>()).IsEqualTo("incident");
        await Assert
            .That(((JsonArray)meta["dataSources"]!)[0]!.GetValue<string>())
            .IsEqualTo("ops-api");
        await Assert.That(meta["usageExamples"]).IsTypeOf<JsonArray>();
        await Assert.That(((JsonArray)meta["usageExamples"]!).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ListResourcesAsync_ExportsSourceQualifiedUrisAndMetadataToSdkClients()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
        });

        var resources = await gatewayServer.Client.ListResourcesAsync();
        var resource = resources.Single(static candidate =>
            candidate.Name == "source-a:repository_overview"
        );

        await Assert.That(resource.Uri).IsNotEqualTo("docs://repository/overview");
        await Assert.That(resource.ProtocolResource?.Meta).IsTypeOf<JsonObject>();

        var meta = (JsonObject)resource.ProtocolResource!.Meta!;
        await Assert.That(meta["sourceId"]!.GetValue<string>()).IsEqualTo("source-a");
        await Assert
            .That(meta["originalUri"]!.GetValue<string>())
            .IsEqualTo("docs://repository/overview");
        await Assert
            .That(meta["resourceName"]!.GetValue<string>())
            .IsEqualTo("repository_overview");
    }

    [Test]
    public async Task SdkClient_CanExecuteMixedWorkflowAgainstGatewayExport()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddTool(
                "local-ops",
                TestFunctionFactory.CreateFunction(
                    LookupIncidentStatus,
                    "incident_status_lookup",
                    "Inspect incident state by identifier.",
                    new Dictionary<string, object?>
                    {
                        ["DisplayName"] = "Lookup incident status",
                        ["categories"] = OperationsCategory,
                        ["readOnly"] = true,
                        ["idempotent"] = true,
                    }
                )
            );
        });

        var tools = await gatewayServer.Client.ListToolsAsync();
        var readOnlyTool = tools
            .Where(static tool => tool.ProtocolTool?.Annotations?.ReadOnlyHint == true)
            .OrderBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .First(static tool => tool.Name == "local-ops:incident_status_lookup");
        var upstreamTool = tools.Single(static tool =>
            tool.Name == "source-a:github_repository_search"
        );

        var localResult = await gatewayServer.Client.CallToolAsync(
            readOnlyTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "incident-42" }
        );
        var upstreamResult = await gatewayServer.Client.CallToolAsync(
            upstreamTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "managedcode" }
        );
        var prompts = await gatewayServer.Client.ListPromptsAsync();
        var prompt = await gatewayServer.Client.GetPromptAsync(
            "source-a:repository_triage_system_prompt",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["repository"] = "ManagedCode/MCPGateway",
            }
        );
        var resources = await gatewayServer.Client.ListResourcesAsync();
        var repositoryOverview = resources.Single(static resource =>
            resource.Name == "source-a:repository_overview"
        );
        var resourceRead = await gatewayServer.Client.ReadResourceAsync(repositoryOverview.Uri);

        await Assert.That(localResult.IsError).IsFalse();
        await Assert.That(localResult.Content.Count).IsEqualTo(1);
        await Assert
            .That(((TextContentBlock)localResult.Content[0]).Text)
            .Contains("incident:incident-42");
        await Assert.That(upstreamResult.IsError).IsFalse();
        await Assert.That(upstreamResult.StructuredContent).IsNotNull();
        await Assert
            .That(
                prompts.Any(static candidate =>
                    candidate.Name == "source-a:repository_triage_system_prompt"
                )
            )
            .IsTrue();
        await Assert.That(resourceRead.Contents.Count).IsEqualTo(1);
        await Assert.That(resourceRead.Contents[0]).IsTypeOf<TextResourceContents>();
        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(prompt.Messages[0].Content).IsTypeOf<TextContentBlock>();
    }

    [Test]
    public async Task SdkClient_CanExecuteComplexStructuredWorkflowAcrossLocalAndUpstreamTools()
    {
        await using var repositoryServer = await TestMcpServerHost.StartAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("repo", repositoryServer.Client, disposeClient: false);
            options.AddMcpClient("ops", operationsServer.Client, disposeClient: false);
            options.AddTool(
                "local-ops",
                TestFunctionFactory.CreateFunction(
                    PlanIncidentMitigation,
                    "incident_plan_mitigation",
                    "Plan a structured mitigation workflow for an active incident.",
                    new Dictionary<string, object?>
                    {
                        ["DisplayName"] = "Plan incident mitigation",
                        ["categories"] = RecoveryCategories,
                        ["tags"] = RecoveryTags,
                        ["dataSources"] = RecoveryDataSources,
                        ["usageExamples"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["input"] =
                                    "plan mitigation for incident 42 in prod-eu requested by alice@example.com",
                                ["output"] = "{\"summary\":\"dry-run failover plan\"}",
                                ["description"] =
                                    "Build a dry-run mitigation plan before failover.",
                            },
                            new Dictionary<string, object?>
                            {
                                ["input"] =
                                    "prepare checkout-api failover for incident 77 in prod-us",
                                ["output"] = "{\"steps\":[\"page\",\"bridge\",\"validate\"]}",
                                ["description"] =
                                    "Plan a recovery workflow for a production incident.",
                            },
                        },
                        ["readOnly"] = false,
                        ["idempotent"] = true,
                        ["openWorld"] = true,
                        ["costTier"] = "Medium",
                        ["latencyTier"] = "Medium",
                    }
                )
            );
        });

        var tools = await gatewayServer.Client.ListToolsAsync();
        var planTool = tools.Single(static tool =>
            tool.Name == "local-ops:incident_plan_mitigation"
        );
        var requiredArguments = planTool
            .ProtocolTool!.InputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Where(static item => item is not null)
            .Cast<string>()
            .ToArray();

        var planResult = await gatewayServer.Client.CallToolAsync(
            planTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["incidentId"] = "incident-42",
                ["environment"] = "prod-eu",
                ["requestedBy"] = "alice@example.com",
                ["dryRun"] = true,
            }
        );
        var deploymentResult = await gatewayServer.Client.CallToolAsync(
            "ops:deployment_status_lookup",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["environment"] = "prod-eu" }
        );
        var deploymentPrompt = await gatewayServer.Client.GetPromptAsync(
            "ops:deployment_review_system_prompt",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["environment"] = "prod-eu" }
        );
        var repositoryPrompt = await gatewayServer.Client.GetPromptAsync(
            "repo:repository_triage_system_prompt",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["repository"] = "ManagedCode/MCPGateway",
                ["locale"] = "uk-UA",
            }
        );

        await Assert
            .That(requiredArguments)
            .IsEquivalentTo(["incidentId", "environment", "requestedBy"]);
        await Assert.That(planResult.IsError).IsFalse();
        await Assert.That(planResult.StructuredContent).IsNotNull();
        await Assert.That(planResult.Content.Count).IsEqualTo(1);
        await Assert.That(((TextContentBlock)planResult.Content[0]).Text).Contains("incident-42");

        var planPayload = JsonSerializer.SerializeToElement(planResult.StructuredContent);
        await Assert
            .That(planPayload.GetProperty("incidentId").GetString())
            .IsEqualTo("incident-42");
        await Assert.That(planPayload.GetProperty("dryRun").GetBoolean()).IsTrue();
        await Assert.That(planPayload.GetProperty("steps").GetArrayLength()).IsEqualTo(4);
        await Assert.That(deploymentResult.IsError).IsFalse();
        await Assert.That(deploymentResult.StructuredContent).IsNotNull();
        await Assert.That(deploymentPrompt.Messages.Count).IsEqualTo(1);
        await Assert.That(repositoryPrompt.Messages.Count).IsEqualTo(1);
        await Assert
            .That(((TextContentBlock)deploymentPrompt.Messages[0].Content).Text)
            .Contains("prod-eu");
        await Assert
            .That(((TextContentBlock)repositoryPrompt.Messages[0].Content).Text)
            .Contains("uk-UA");
    }

    private static string LookupIncidentStatus(string query) => $"incident:{query}";

    private static object PlanIncidentMitigation(
        string incidentId,
        string environment,
        string requestedBy,
        bool dryRun = true
    ) =>
        new
        {
            incidentId,
            environment,
            requestedBy,
            dryRun,
            steps = new[]
            {
                "page-primary-on-call",
                "open-war-room",
                $"validate-{environment}",
                $"prepare-failover-{incidentId}",
            },
            coordination = new
            {
                chatChannel = $"incidents-{incidentId}",
                reviewPrompt = "ops:deployment_review_system_prompt",
            },
        };
}
