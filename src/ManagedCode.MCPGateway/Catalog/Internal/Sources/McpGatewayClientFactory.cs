using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

#pragma warning disable MCPEXP003

internal static class McpGatewayClientFactory
{
    private const string ClientName = "managedcode-mcpgateway";
    private const string UnknownClientVersion = "unknown";
    private static readonly string ClientVersion = ResolveClientVersion();

    public static McpClientOptions CreateClientOptions() =>
        new()
        {
            ClientInfo = new Implementation { Name = ClientName, Version = ClientVersion },
            ProtocolVersion = McpGatewayMcpProtocolConstants.CurrentProtocolVersion,
            Capabilities = new ClientCapabilities
            {
                Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [McpApps.ExtensionId] = JsonSerializer.SerializeToElement(
                        new McpUiClientCapabilities
                        {
                            MimeTypes = [McpApps.HtmlMimeType],
                        },
                        McpApps.SerializerOptions
                    ),
                },
            },
        };

    private static string ResolveClientVersion() =>
        typeof(McpGatewayClientFactory)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(McpGatewayClientFactory).Assembly.GetName().Version?.ToString()
        ?? UnknownClientVersion;
}

#pragma warning restore MCPEXP003
