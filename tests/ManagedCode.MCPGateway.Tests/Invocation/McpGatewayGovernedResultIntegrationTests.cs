using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGovernedResultIntegrationTests
{
    [TUnit.Core.Test]
    public async Task Real_SDK_oversized_result_returns_handle_and_reader_checks_live_publication_revocation()
    {
        await using var host = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.Governance.ManagedCatalog = true;
            options.Governance.MaximumResponseBytes = 1024;
            options.AddTool("records", AIFunctionFactory.Create(static () => new { id = "APPROVED_ID", payload = new string('x', 2000) },
                new AIFunctionFactoryOptions { Name = "record" }));
        }, services =>
        {
            services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
            services.AddSingleton<IMcpGatewayAccessPolicy, Policy>();
            services.AddMcpGatewayInMemoryCatalogStore();
            services.AddMcpGatewayInMemoryResultStore();
            services.Configure<McpGatewayMcpServerExportOptions>(options => options.Mode = McpGatewayMcpServerExportMode.DiscoveryOnly);
        });
        var publication = host.Services.GetRequiredService<IMcpGatewayCatalogPublication>();
        var definitions = await publication.InspectCandidateAsync();
        var approved = definitions.Select(definition => definition with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await Assert.That((await publication.PublishAsync(approved, 0, "review-1")).IsPublished).IsTrue();
        var invoke = await host.Client.CallToolAsync(McpGatewayToolSet.DefaultInvokeToolName, new Dictionary<string, object?>
        {
            ["toolId"] = definitions.Single().ToolId,
            ["responseOptions"] = new McpGatewayResponseOptions { MaxBytes = 1024, StoreOversizedResult = true },
        });
        var envelope = JsonDocument.Parse(((TextContentBlock)invoke.Content[0]).Text).RootElement;
        var handle = envelope.GetProperty("resultHandle").GetString()!;
        await Assert.That(envelope.TryGetProperty("output", out _)).IsFalse();
        var read = await host.Client.CallToolAsync(McpGatewayDiscoveryToolSet.ResultToolName, new Dictionary<string, object?>
        {
            ["handle"] = handle,
            ["response"] = new McpGatewayResponseOptions { MaxBytes = 1024, SelectPaths = ["/id"] },
        });
        await Assert.That(((TextContentBlock)read.Content[0]).Text).Contains("APPROVED_ID");
        await Assert.That((await publication.RevokeAsync(definitions.Single().ToolId, 1)).IsPublished).IsTrue();
        await Assert.That(await host.Services.GetRequiredService<IMcpGatewayResultReader>().ReadAsync(handle,
            new() { SelectPaths = ["/id"] })).IsNull();
    }

    [TUnit.Core.Test]
    public async Task Output_schema_violation_is_rejected_after_one_execution_with_no_raw_output()
    {
        var function = new InvalidOutputFunction();
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddTool("records", function));
        var result = await provider.GetRequiredService<Abstractions.IMcpGateway>().InvokeAsync(new(ToolName: "record"));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.ResultRejected);
        await Assert.That(result.Output).IsNull();
        await Assert.That(result.McpResult).IsNull();
        await Assert.That(function.Executions).IsEqualTo(1);
    }

    private sealed class InvalidOutputFunction : AIFunction
    {
        public int Executions;
        public override string Name => "record";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        public override JsonElement? ReturnJsonSchema { get; } = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"string\"}},\"additionalProperties\":false}").RootElement.Clone();
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Executions); return ValueTask.FromResult<object?>(new { secret = "SEEDED_PRIVATE_OUTPUT" }); }
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "partition", "v1"));
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(McpGatewayAccessDecision.Allowed);
    }
}
