using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProtocolValidationTests
{
    [Test]
    public async Task Real_SDK_invalid_params_do_not_open_the_source_circuit_or_trigger_a_mutation_retry()
    {
        await using var upstream = await DynamicMcpCatalogHost.StartAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("remote", upstream.Client);
            options.Execution.Enabled = true;
            options.Execution.Sources.Add("remote", new() { FailureThreshold = 1 });
        });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        upstream.RejectInvocationArguments = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await gateway.InvokeAsync(new(ToolName: "lookup"));
            await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.InvalidArguments);
            await Assert.That(result.Error!).DoesNotContain("SEEDED_PRIVATE_VALIDATION");
        }
        await Assert.That(upstream.InvokeCalls).IsEqualTo(2);
        upstream.RejectInvocationArguments = false;
        await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue();
        await Assert.That(upstream.InvokeCalls).IsEqualTo(3);
    }
}
