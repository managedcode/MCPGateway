using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySearchMetadataTests
{
    private static readonly string[] IncidentWarRoomTags =
    [
        "incident-response",
        "war-room",
        "bridge",
    ];

    [Test]
    public async Task SearchAsync_UsesCategoriesTagsAndExamplesForToolDiscovery()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    LookupIncidentStatus,
                    "ticket_status_lookup",
                    "Inspect a support record by identifier."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["operations"],
                    Tags: ["pagerduty", "incident-triage"],
                    DataSources: ["ops-api"],
                    UsageExamples:
                    [
                        new McpGatewayToolExample(
                            "incident 42 status",
                            "{\"status\":\"open\"}",
                            "Check whether an incident is still active."
                        ),
                    ],
                    ReadOnly: true,
                    Idempotent: true,
                    CostTier: McpGatewayToolCostTier.Low,
                    LatencyTier: McpGatewayToolLatencyTier.Low
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    ApplyDeployment,
                    "deployment_apply_plan",
                    "Apply a deployment plan to an environment."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["deployments"],
                    Tags: ["rollout"],
                    DataSources: ["deploy-api"],
                    UsageExamples:
                    [
                        new McpGatewayToolExample(
                            "deploy prod",
                            "{\"jobId\":\"deploy-42\"}",
                            "Start a production rollout."
                        ),
                    ],
                    ReadOnly: false,
                    Idempotent: false,
                    Destructive: true,
                    CostTier: McpGatewayToolCostTier.High,
                    LatencyTier: McpGatewayToolLatencyTier.High
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            new McpGatewaySearchRequest(Query: "pagerduty incident 42 status", MaxResults: 1)
        );

        await Assert.That(searchResult.Matches.Count).IsEqualTo(1);
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:ticket_status_lookup");
        await Assert.That(searchResult.Matches[0].Categories).IsEquivalentTo(["operations"]);
        await Assert.That(searchResult.Matches[0].Tags.Contains("pagerduty")).IsTrue();
        await Assert.That(searchResult.Matches[0].UsageExamples.Count).IsEqualTo(1);
        await Assert.That(searchResult.Matches[0].IsReadOnly).IsTrue();
        await Assert.That(searchResult.Matches[0].CostTier).IsEqualTo(McpGatewayToolCostTier.Low);
    }

    [Test]
    public async Task SearchAsync_HidesDisabledToolsUnlessRequested()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    ResetPassword,
                    "admin_user_reset_password",
                    "Perform a privileged user reset flow."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["identity"],
                    Tags: ["reset-password", "privileged"],
                    UsageExamples:
                    [
                        new McpGatewayToolExample(
                            "reset admin password for alice",
                            "Password reset started for alice."
                        ),
                    ],
                    ReadOnly: false,
                    EnabledByDefault: false
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();

        var defaultSearchResult = await gateway.SearchAsync(
            new McpGatewaySearchRequest(Query: "reset admin password for alice", MaxResults: 3)
        );
        var explicitSearchResult = await gateway.SearchAsync(
            new McpGatewaySearchRequest(
                Query: "reset admin password for alice",
                MaxResults: 3,
                IncludeDisabledTools: true
            )
        );

        await Assert
            .That(
                defaultSearchResult.Matches.Any(static match =>
                    match.ToolId == "local:admin_user_reset_password"
                )
            )
            .IsFalse();
        await Assert.That(explicitSearchResult.Matches.Count).IsEqualTo(1);
        await Assert
            .That(explicitSearchResult.Matches[0].ToolId)
            .IsEqualTo("local:admin_user_reset_password");
        await Assert.That(explicitSearchResult.Matches[0].IsEnabledByDefault).IsFalse();
    }

    [Test]
    public async Task SearchAsync_ParsesMixedSerializedUsageExamplesAndExecutionHints()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    PrepareWarRoom,
                    "incident_war_room_prepare",
                    "Prepare a coordinated incident response plan with paging, bridge creation, and environment safeguards.",
                    new Dictionary<string, object?>
                    {
                        ["category"] = "operations",
                        ["tags"] = IncidentWarRoomTags,
                        ["dataSource"] = "incident-automation",
                        ["usage_examples"] = new object?[]
                        {
                            "open a war room for incident 42",
                            new Dictionary<string, object?>
                            {
                                ["input"] =
                                    "page database on-call and create a bridge for incident 42",
                                ["output"] = "{\"steps\":[\"page-oncall\",\"create-bridge\"]}",
                                ["description"] =
                                    "Escalate responders and open the coordination bridge.",
                            },
                        },
                        ["readOnlyHint"] = false,
                        ["idempotentHint"] = true,
                        ["openWorldHint"] = true,
                        ["costTier"] = "Medium",
                        ["latencyTier"] = "Medium",
                    }
                )
            );
            options.AddTool(
                TestFunctionFactory.CreateFunction(
                    LookupRunbook,
                    "incident_runbook_search",
                    "Search incident runbooks and previous postmortems."
                ),
                new McpGatewayToolSearchHints(
                    Categories: ["docs"],
                    Tags: ["runbook", "postmortem"],
                    ReadOnly: true,
                    Idempotent: true
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            new McpGatewaySearchRequest(
                Query: "page database on-call and create a bridge for incident 42",
                MaxResults: 2
            )
        );

        await Assert.That(searchResult.Matches.Count).IsEqualTo(2);
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("local:incident_war_room_prepare");
        await Assert.That(searchResult.Matches[0].Categories).IsEquivalentTo(["operations"]);
        await Assert
            .That(searchResult.Matches[0].DataSources)
            .IsEquivalentTo(["incident-automation"]);
        await Assert.That(searchResult.Matches[0].UsageExamples.Count).IsEqualTo(2);
        await Assert
            .That(searchResult.Matches[0].UsageExamples[0].Input)
            .IsEqualTo("open a war room for incident 42");
        await Assert
            .That(searchResult.Matches[0].UsageExamples[1].Description)
            .IsEqualTo("Escalate responders and open the coordination bridge.");
        await Assert.That(searchResult.Matches[0].IsReadOnly).IsFalse();
        await Assert.That(searchResult.Matches[0].IsIdempotent).IsTrue();
        await Assert.That(searchResult.Matches[0].IsOpenWorld).IsTrue();
        await Assert
            .That(searchResult.Matches[0].CostTier)
            .IsEqualTo(McpGatewayToolCostTier.Medium);
        await Assert
            .That(searchResult.Matches[0].LatencyTier)
            .IsEqualTo(McpGatewayToolLatencyTier.Medium);
    }

    private static string LookupIncidentStatus(string query) => $"status:{query}";

    private static string ApplyDeployment(string query) => $"deploy:{query}";

    private static string ResetPassword(string query) => $"reset:{query}";

    private static string PrepareWarRoom(string query) => $"war-room:{query}";

    private static string LookupRunbook(string query) => $"runbook:{query}";
}
