using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResponseDeliveryTests
{
    [TUnit.Core.Test]
    public async Task Projection_removes_secret_from_all_MCP_representations_and_keeps_array_positions()
    {
        var upstream = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { id = "A123", secret = "SEEDED_SECRET", items = new[] { new { sku = "A", secret = "SEEDED_SECRET" }, new { sku = "B", secret = "SEEDED_SECRET" } } }),
            Content = [new TextContentBlock { Text = "SEEDED_SECRET" }],
            Meta = new() { ["secret"] = "SEEDED_SECRET" },
        };
        await using var server = await TestMcpServerHost.StartResultAsync(upstream);
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddMcpClient("results", server.Client));
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "result_probe")
        { ResponseOptions = new() { SelectPaths = ["/id", "/items/1/sku"] } });
        var json = JsonSerializer.Serialize(result.McpResult, McpGatewayJsonSerializer.Options);
        await Assert.That(json.Contains("SEEDED_SECRET", StringComparison.Ordinal)).IsFalse();
        await Assert.That(result.McpResult!.StructuredContent!.Value.GetProperty("items")[0].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(result.McpResult.StructuredContent!.Value.GetProperty("items")[1].GetProperty("sku").GetString()).IsEqualTo("B");
        await Assert.That(result.DeliveredBytes > 0).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task Tool_error_remains_serializable_through_meta_tool_and_downstream_SDK()
    {
        await using var upstream = await TestMcpServerHost.StartResultAsync(new()
        { IsError = true, Content = [new TextContentBlock { Text = "business error" }] });
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddMcpClient("errors", upstream.Client));
        var function = (AIFunction)provider.GetRequiredService<McpGatewayToolSet>().CreateTools()
            .Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
        var result = (JsonElement)(await function.InvokeAsync(new AIFunctionArguments { ["toolId"] = "result_probe" }))!;
        await Assert.That(result.GetProperty("outcome").GetString()).IsEqualTo(nameof(McpGatewayInvocationOutcome.ToolError));
        await Assert.That(result.GetProperty("errorCode").GetString()).IsEqualTo("tool_error");
        await using var gatewayHost = await GatewayMcpServerHost.StartAsync(options => options.AddMcpClient("errors", upstream.Client));
        var downstream = await gatewayHost.Client.CallToolAsync("result_probe", new Dictionary<string, object?>());
        await Assert.That(downstream.IsError).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task Oversized_JSON_fails_safely_without_string_truncation()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            options.AddTool(AIFunctionFactory.Create(static () => new { secret = new string('x', 1000) }, new AIFunctionFactoryOptions { Name = "large_result" })));
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "large_result")
        { ResponseOptions = new() { MaxBytes = 100 } });
        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("result_too_large");
        await Assert.That(result.Output).IsNull();
        await Assert.That(result.McpResult).IsNull();
    }

    [TUnit.Core.Test]
    public async Task Result_policy_removes_raw_protocol_alternatives_before_any_delivery()
    {
        await using var upstream = await TestMcpServerHost.StartResultAsync(new()
        { Content = [new TextContentBlock { Text = "SEEDED_SECRET" }], Meta = new() { ["secret"] = "SEEDED_SECRET" } });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => options.AddMcpClient("results", upstream.Client));
        services.AddSingleton<IMcpGatewayResultPolicy, SafeResultPolicy>();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "result_probe"));
        await Assert.That(JsonSerializer.Serialize(result.McpResult, McpGatewayJsonSerializer.Options)).DoesNotContain("SEEDED_SECRET");
        await Assert.That(((JsonElement)result.Output!).GetProperty("status").GetString()).IsEqualTo("approved");
    }

    private sealed class SafeResultPolicy : IMcpGatewayResultPolicy
    {
        public ValueTask<McpGatewayApprovedResult> ApproveAsync(McpGatewayExecutionContext context, McpGatewayToolDescriptor tool,
            JsonElement? output, CallToolResult? protocolResult, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayApprovedResult(JsonSerializer.SerializeToElement(new { status = "approved" })));
    }
}
