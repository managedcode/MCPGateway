using System.Runtime.CompilerServices;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayMcpServerIdentity
{
    public static string GetKey(ModelContextProtocol.Server.McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return !string.IsNullOrWhiteSpace(server.SessionId)
            ? server.SessionId
            : $"server:{RuntimeHelpers.GetHashCode(server)}";
    }
}
