using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayBundledSchemaTests
{
    [Test]
    public async Task Absolute_bundled_schema_references_validate_real_proxy_arguments_without_network_resolution()
    {
        var function = new Function();
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddTool("local", function));
        var gateway = provider.GetRequiredService<ManagedCode.MCPGateway.Abstractions.IMcpGateway>();
        var invalid = await gateway.InvokeAsync(new(ToolId: function.Name, Arguments: new Dictionary<string, object?> { ["count"] = "invalid" }));
        await Assert.That(invalid.Outcome).IsEqualTo(McpGatewayInvocationOutcome.InvalidArguments);
        await Assert.That(function.Calls).IsEqualTo(0);
        var proxy = (AIFunction)(await provider.GetRequiredService<McpGatewayDiscoveryToolSet>().CreateSchemaFaithfulToolsAsync([function.Name])).Single();
        await Assert.That(proxy.JsonSchema.GetRawText()).IsEqualTo(function.JsonSchema.GetRawText());
        var result = (JsonElement)(await proxy.InvokeAsync(new AIFunctionArguments { ["count"] = 42 }))!;
        await Assert.That(result.GetProperty("isSuccess").GetBoolean()).IsTrue();
        await Assert.That(function.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Unbundled_external_reference_is_rejected_before_any_fetch()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"$ref":"https://private.example/credential-schema"}}}""");
        try
        { _ = McpGatewaySchemaValidator.Compile(schema.RootElement); Assert.Fail("An external reference must require an explicit bundle."); }
        catch (InvalidOperationException exception) { await Assert.That(exception.Message).Contains("bundled"); }
    }

    [Test]
    public async Task Invalid_output_schema_rejects_delivery_after_one_dispatch_without_a_retry()
    {
        var function = new Function(externalOutput: true);
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddTool("local", function));
        var result = await provider.GetRequiredService<ManagedCode.MCPGateway.Abstractions.IMcpGateway>().InvokeAsync(
            new(ToolId: function.Name, Arguments: new Dictionary<string, object?> { ["count"] = 42 }));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.ResultRejected);
        await Assert.That(result.Output).IsNull();
        await Assert.That(result.McpResult).IsNull();
        await Assert.That(function.Calls).IsEqualTo(1);
    }

    private sealed class Function(bool externalOutput = false) : AIFunction
    {
        public int Calls;
        public override string Name => "bundled_count";
        public override JsonElement? ReturnJsonSchema => externalOutput
            ? JsonDocument.Parse("""{"$ref":"https://private.example/output"}""").RootElement.Clone() : null;
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""
            {"$id":"https://schemas.example/root","type":"object","required":["count"],"additionalProperties":false,
             "properties":{"count":{"$ref":"https://schemas.example/count"}},
             "$defs":{"count":{"$id":"https://schemas.example/count","type":"integer","minimum":1}}}
            """).RootElement.Clone();
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return ValueTask.FromResult<object?>(arguments["count"]); }
    }
}
