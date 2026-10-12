using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySchemaBudgetTests
{
    [Test]
    public async Task Oversized_schema_reports_an_explicit_budget_failure_and_configured_larger_lookup_is_exact()
    {
        var function = new Function();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        { options.MaximumToolSchemaBytes = 2048; options.AddTool("local", function); });
        var discovery = provider.GetRequiredService<IMcpGatewayDiscovery>();
        try
        { await discovery.GetToolSchemaAsync(function.Name); Assert.Fail("The schema must not be truncated or exceed its configured budget."); }
        catch (McpGatewaySchemaTooLargeException exception)
        {
            await Assert.That(exception.ActualBytes).IsGreaterThan(exception.MaximumBytes);
            await Assert.That(exception.SchemaHash.Length).IsEqualTo(64);
        }
        var schemaTool = (AIFunction)provider.GetRequiredService<McpGatewayDiscoveryToolSet>().CreateTools()
            .Single(tool => tool.Name == McpGatewayDiscoveryToolSet.SchemaToolName);
        var failure = McpGatewayJsonSerializer.TrySerializeToElement(await schemaTool.InvokeAsync(new AIFunctionArguments { ["toolId"] = function.Name }))!.Value;
        await Assert.That(failure.GetProperty("errorCode").GetString()).IsEqualTo(McpGatewaySchemaTooLargeException.ErrorCode);
        await Assert.That(failure.GetRawText().Length < 2048).IsTrue();
        await using var larger = GatewayTestServiceProviderFactory.Create(options => options.AddTool("local", function));
        var complete = await larger.GetRequiredService<IMcpGatewayDiscovery>().GetToolSchemaAsync(function.Name);
        await Assert.That(complete!.Tool.InputSchema.GetRawText()).IsEqualTo(function.JsonSchema.GetRawText());
    }
    private sealed class Function : AIFunction
    {
        public override string Name => "large_schema";
        public override JsonElement JsonSchema { get; } = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = new { type = "string", @enum = new[] { new string('x', 10000) } } },
        });
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(null);
    }
}
