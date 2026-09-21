using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProtocolResultTests
{
    [TUnit.Core.Test]
    [TUnit.Core.Arguments(false)]
    [TUnit.Core.Arguments(true)]
    public async Task Invocation_preserves_complete_upstream_protocol_result(bool isError)
    {
        var expected = new CallToolResult
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = "First" }, new TextContentBlock { Text = "Second" }],
            StructuredContent = JsonSerializer.SerializeToElement(new { status = "observed" }),
            Meta = new() { ["display"] = "details" }
        };
        await using var server = await TestMcpServerHost.StartResultAsync(expected);
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            options.AddMcpClient("protocol", server.Client, disposeClient: false));

        var actual = await provider.GetRequiredService<IMcpGateway>()
            .InvokeAsync(new McpGatewayInvokeRequest(ToolName: "result_probe"));

        await Assert.That(actual.IsSuccess).IsTrue();
        await Assert.That(actual.McpResult).IsNotNull();
        await Assert.That(actual.McpResult!.IsError).IsEqualTo(isError);
        await Assert.That(JsonSerializer.Serialize(actual.McpResult.Content, McpJsonUtilities.DefaultOptions))
            .IsEqualTo(JsonSerializer.Serialize(expected.Content, McpJsonUtilities.DefaultOptions));
        await Assert.That(actual.McpResult.StructuredContent!.Value.GetRawText())
            .IsEqualTo(expected.StructuredContent!.Value.GetRawText());
        await Assert.That(actual.McpResult.Meta!["display"]!.GetValue<string>()).IsEqualTo("details");
    }
}
