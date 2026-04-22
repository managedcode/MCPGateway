using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceCatalogTests
{
    [TUnit.Core.Test]
    public async Task ListResourcesAsync_ReturnsDescriptorsFromMcpSource()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var resourceCatalog = serviceProvider.GetRequiredService<IMcpGatewayResourceCatalog>();

        var resources = await resourceCatalog.ListResourcesAsync();
        var descriptor = resources.Single(static resource =>
            resource.ResourceUri == "docs://repository/overview"
        );

        await Assert.That(resources.Count).IsEqualTo(2);
        await Assert.That(descriptor.SourceKind).IsEqualTo(McpGatewaySourceKind.CustomMcpClient);
        await Assert.That(descriptor.ResourceName).IsEqualTo("repository_overview");
        await Assert.That(descriptor.DisplayName).IsEqualTo("Repository overview");
        await Assert.That(descriptor.MimeType).IsEqualTo("text/markdown");
    }

    [TUnit.Core.Test]
    public async Task ListResourceTemplatesAsync_ReturnsTemplateDescriptorsFromMcpSource()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var resourceCatalog = serviceProvider.GetRequiredService<IMcpGatewayResourceCatalog>();

        var templates = await resourceCatalog.ListResourceTemplatesAsync();
        var descriptor = templates.Single(static template =>
            template.UriTemplate == "docs://issues/{id}"
        );

        await Assert.That(templates.Count).IsEqualTo(1);
        await Assert.That(descriptor.SourceKind).IsEqualTo(McpGatewaySourceKind.CustomMcpClient);
        await Assert.That(descriptor.ResourceName).IsEqualTo("issue_detail");
        await Assert.That(descriptor.DisplayName).IsEqualTo("Issue detail");
        await Assert.That(descriptor.MimeType).IsEqualTo("application/json");
    }

    [TUnit.Core.Test]
    public async Task ReadResourceAsync_ReturnsRenderedTemplatedResourceContents()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var resourceCatalog = serviceProvider.GetRequiredService<IMcpGatewayResourceCatalog>();

        var resource = await resourceCatalog.ReadResourceAsync(
            new McpGatewayResourceRequest("test-mcp", "docs://issues/42")
        );

        await Assert.That(resource).IsNotNull();
        await Assert.That(resource!.SourceKind).IsEqualTo(McpGatewaySourceKind.CustomMcpClient);
        await Assert.That(resource.ResourceUri).IsEqualTo("docs://issues/42");
        await Assert.That(resource.Contents.Count).IsEqualTo(1);
        await Assert.That(resource.Contents[0]).IsTypeOf<TextResourceContents>();
        await Assert.That(((TextResourceContents)resource.Contents[0]).Text).Contains("\"id\":\"42\"");
    }

    [TUnit.Core.Test]
    public async Task FactoryCreatedInstance_ExposesResourceCatalog()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(_ => { });
        var factory = serviceProvider.GetRequiredService<IMcpGatewayFactory>();

        await using var gatewayHost = factory.Create(options =>
        {
            options.AddMcpClient("factory-mcp", serverHost.Client, disposeClient: false);
        });

        var resources = await gatewayHost.ResourceCatalog.ListResourcesAsync();

        await Assert
            .That(
                resources.Any(static resource =>
                    resource.ResourceUri == "docs://repository/overview"
                )
            )
            .IsTrue();
    }
}
