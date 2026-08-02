#pragma warning disable MCPEXP003

using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Extensions.Apps;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayClientFactoryTests
{
    [Test]
    public async Task CreateClientOptions_UsesAssemblyBuildVersionAndAppsCapability()
    {
        var clientOptions = McpGatewayClientFactory.CreateClientOptions();
        var expectedVersion =
            typeof(McpGatewayClientFactory)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(McpGatewayClientFactory).Assembly.GetName().Version?.ToString();

        await Assert.That(clientOptions.ClientInfo?.Version).IsEqualTo(expectedVersion);
        await Assert
            .That(clientOptions.ProtocolVersion)
            .IsEqualTo(McpGatewayMcpProtocolConstants.CurrentProtocolVersion);
        var appsCapability = JsonSerializer
            .SerializeToElement(
                clientOptions.Capabilities?.Extensions?[McpApps.ExtensionId],
                McpGatewayJsonSerializer.Options
            )
            .Deserialize<McpUiClientCapabilities>(McpApps.SerializerOptions);

        await Assert.That(appsCapability).IsNotNull();
        await Assert.That(appsCapability!.MimeTypes).Contains(McpApps.HtmlMimeType);
    }
}

#pragma warning restore MCPEXP003
