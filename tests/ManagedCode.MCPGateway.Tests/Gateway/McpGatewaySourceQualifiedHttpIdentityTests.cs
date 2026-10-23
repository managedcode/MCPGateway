using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySourceQualifiedHttpIdentityTests
{
    private const string UpstreamTool = "streamable_http_lookup";
    private const string FirstSource = "first-server";
    private const string SecondSource = "second-server";
    private const string FirstTool = "first-server_streamable_http_lookup";
    private const string SecondTool = "second-server_streamable_http_lookup";

    [Test]
    public async Task HTTP_tool_identity_survives_another_server_with_the_same_upstream_method()
    {
        await using var first = await HttpMcpServerHost.StartAsync();
        await using var second = await HttpMcpServerHost.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway();
        await using var provider = services.BuildServiceProvider();
        await using var gateway = provider.GetRequiredService<IMcpGatewayFactory>().Create(options =>
            options.AddHttpServer(new() { SourceId = FirstSource, Endpoint = first.Endpoint, UseSourceQualifiedToolIds = true }));
        var original = (await gateway.Gateway.ListToolsAsync()).Single();
        await Assert.That(original.ToolId).IsEqualTo(FirstTool);
        await Assert.That(original.ToolName).IsEqualTo(UpstreamTool);
        gateway.Registry.AddHttpServer(new() { SourceId = SecondSource, Endpoint = second.Endpoint, UseSourceQualifiedToolIds = true });
        var tools = await gateway.Gateway.ListToolsAsync();
        await Assert.That(tools.Select(tool => tool.ToolId)).IsEquivalentTo([FirstTool, SecondTool]);
        var result = await gateway.Gateway.InvokeAsync(new(ToolId: original.ToolId,
            Arguments: new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("persisted selection", McpGatewayJsonSerializer.Options) }));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.SourceId).IsEqualTo(FirstSource);
        var direct = await new McpGatewayDiscoveryToolSet(gateway.Gateway, gateway.Discovery)
            .CreateSchemaFaithfulToolsAsync([original.ToolId]);
        await Assert.That(direct.Single().Name).IsEqualTo(original.ToolId);
    }
}
