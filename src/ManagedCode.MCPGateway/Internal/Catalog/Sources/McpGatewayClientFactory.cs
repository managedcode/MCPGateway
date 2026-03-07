using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayClientFactory
{
    private const string ClientName = "managedcode-mcpgateway";
    private static readonly string ClientVersion = ResolveClientVersion();

    public static McpClientOptions CreateClientOptions()
        => new()
        {
            ClientInfo = new Implementation
            {
                Name = ClientName,
                Version = ClientVersion
            }
        };

    private static string ResolveClientVersion()
        => typeof(McpGatewayClientFactory).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(McpGatewayClientFactory).Assembly.GetName().Version?.ToString()
           ?? "unknown";
}
