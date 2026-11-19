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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Native_invoke_reports_success_and_controls_delivery_of_a_large_publication(bool project)
    {
        var publications = 0;
        await using var host = await GatewayMcpServerHost.StartAsync(options =>
            options.AddTool(AIFunctionFactory.Create(
                () => new { version = Interlocked.Increment(ref publications), payload = new string('x', 1000) },
                new AIFunctionFactoryOptions { Name = "publish_record" })),
            services => services.Configure<McpGatewayMcpServerExportOptions>(
                options => options.Mode = McpGatewayMcpServerExportMode.DiscoveryOnly));
        var invoke = (await host.Client.ListToolsAsync()).Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
        using var args = JsonDocument.Parse(project
            ? """{"toolId":"publish_record","responseOptions":{"maxBytes":256,"selectPaths":["/version"]}}"""
            : """{"toolId":"publish_record","responseOptions":{"maxBytes":256}}""");
        await Assert.That(invoke.JsonSchema.GetProperty("properties").TryGetProperty("responseOptions", out _)).IsTrue();
        var result = await host.Client.CallToolAsync(invoke.Name, args.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value));
        await Assert.That(result.IsError == true).IsFalse();
        using var json = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
        var envelope = json.RootElement;
        await Assert.That(envelope.GetProperty("isSuccess").GetBoolean()).IsTrue();
        await Assert.That(envelope.GetProperty("outcome").GetString()).IsEqualTo(nameof(McpGatewayInvocationOutcome.Succeeded));
        await Assert.That(envelope.GetRawText()).DoesNotContain("payload");
        await Assert.That(publications).IsEqualTo(1);
        if (project)
        {
            await Assert.That(envelope.GetProperty("output").GetProperty("version").GetInt32()).IsEqualTo(1);
        }
        else
        {
            await Assert.That(envelope.GetProperty("errorCode").GetString()).IsEqualTo("result_too_large");
            await Assert.That(envelope.TryGetProperty("output", out _)).IsFalse();
            await Assert.That(envelope.GetProperty("deliveredBytes").GetInt32()).IsEqualTo(0);
        }
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
