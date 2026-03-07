using System.Text.Json;

using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayInvocationTests
{
    [TUnit.Core.Test]
    public async Task InvokeAsync_InvokesStructuredMcpToolAndMapsQueryArgument()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:github_repository_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<JsonElement>();

        var output = (JsonElement)invokeResult.Output!;
        await Assert.That(GetJsonProperty(output, "query").GetString()).IsEqualTo("managedcode");
        await Assert.That(GetJsonProperty(output, "source").GetString()).IsEqualTo("mcp");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PassesContextMetaToMcpToolRequests()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:github_repository_search",
            Query: "managedcode",
            ContextSummary: "user is on repository settings page",
            Context: new Dictionary<string, object?>
            {
                ["page"] = "settings",
                ["domain"] = "github"
            }));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(serverHost.CapturedMeta.Count > 0).IsTrue();

        var payload = serverHost.CapturedMeta[^1];
        await Assert.That(payload.TryGetPropertyValue("managedCodeMcpGateway", out var gatewayNode)).IsTrue();

        var gatewayMeta = gatewayNode!.AsObject();
        await Assert.That(gatewayMeta["query"]!.GetValue<string>()).IsEqualTo("managedcode");
        await Assert.That(gatewayMeta["contextSummary"]!.GetValue<string>()).IsEqualTo("user is on repository settings page");
        await Assert.That(gatewayMeta["context"]!["page"]!.GetValue<string>()).IsEqualTo("settings");
        await Assert.That(gatewayMeta["context"]!["domain"]!.GetValue<string>()).IsEqualTo("github");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ParsesJsonTextContentFromMcpTool()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:json_text_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<JsonElement>();

        var output = (JsonElement)invokeResult.Output!;
        await Assert.That(GetJsonProperty(output, "query").GetString()).IsEqualTo("managedcode");
        await Assert.That(GetJsonProperty(output, "source").GetString()).IsEqualTo("text-json");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsPlainTextWhenMcpTextContentIsNotJson()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:plain_text_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("plain:managedcode");
    }
}
