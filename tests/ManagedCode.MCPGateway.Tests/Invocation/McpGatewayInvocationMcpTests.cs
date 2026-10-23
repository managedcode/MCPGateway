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

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: "github_repository_search",
                Query: "managedcode"
            )
        );

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

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: "github_repository_search",
                Query: "managedcode",
                ContextSummary: "user is on repository settings page",
                Context: new Dictionary<string, JsonElement>
                {
                    ["page"] = JsonSerializer.SerializeToElement("settings", McpGatewayJsonSerializer.Options),
                    ["domain"] = JsonSerializer.SerializeToElement("github", McpGatewayJsonSerializer.Options),
                }
            )
        );

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(serverHost.CapturedMeta.Count > 0).IsTrue();

        var payload = serverHost.CapturedMeta[^1];
        await Assert
            .That(payload.TryGetPropertyValue("managedCodeMcpGateway", out var gatewayNode))
            .IsTrue();

        var gatewayMeta = gatewayNode!.AsObject();
        await Assert.That(gatewayMeta["query"]!.GetValue<string>()).IsEqualTo("managedcode");
        await Assert
            .That(gatewayMeta["contextSummary"]!.GetValue<string>())
            .IsEqualTo("user is on repository settings page");
        await Assert
            .That(gatewayMeta["context"]!["page"]!.GetValue<string>())
            .IsEqualTo("settings");
        await Assert
            .That(gatewayMeta["context"]!["domain"]!.GetValue<string>())
            .IsEqualTo("github");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PreservesExplicitJsonNullContextMeta()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: "github_repository_search",
                Query: "managedcode",
                Context: new Dictionary<string, JsonElement> { ["optional"] = JsonSerializer.SerializeToElement((object?)null, McpGatewayJsonSerializer.Options) }
            )
        );

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(serverHost.CapturedMeta.Count > 0).IsTrue();

        var payload = serverHost.CapturedMeta[^1];
        await Assert
            .That(payload.TryGetPropertyValue("managedCodeMcpGateway", out var gatewayNode))
            .IsTrue();

        var gatewayMeta = gatewayNode!.AsObject();
        await Assert.That(gatewayMeta["query"]!.GetValue<string>()).IsEqualTo("managedcode");
        await Assert.That(gatewayMeta["context"]!.AsObject().ContainsKey("optional")).IsTrue();
        await Assert.That(gatewayMeta["context"]!["optional"]).IsNull();
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

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(ToolId: "json_text_search", Query: "managedcode")
        );

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

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(ToolId: "plain_text_search", Query: "managedcode")
        );

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("plain:managedcode");
    }

}
