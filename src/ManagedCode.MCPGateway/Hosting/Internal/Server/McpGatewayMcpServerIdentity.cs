using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayMcpServerIdentity
{
    private const string IdentityFormat = "N";
    private static readonly ConditionalWeakTable<McpServerOptions, ServerIdentity> Identities = new();

    public static string GetInstanceId(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return Identities.GetValue(
            server.ServerOptions,
            static _ => new ServerIdentity(Guid.NewGuid().ToString(IdentityFormat))
        ).Value;
    }

    private sealed record ServerIdentity(string Value);
}
