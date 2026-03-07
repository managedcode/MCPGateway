using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayClientFactory
{
    private const string ClientName = "managedcode-mcpgateway";
    private const string ClientVersion = "1.0.0";

    public static McpClientOptions CreateClientOptions()
        => new()
        {
            ClientInfo = new Implementation
            {
                Name = ClientName,
                Version = ClientVersion
            }
        };
}
