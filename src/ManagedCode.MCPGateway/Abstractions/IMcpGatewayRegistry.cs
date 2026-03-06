using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayRegistry
{
    void AddTool(string sourceId, AITool tool, string? displayName = null);

    void AddTool(AITool tool, string sourceId = "local", string? displayName = null);

    void AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null);

    void AddTools(IEnumerable<AITool> tools, string sourceId = "local", string? displayName = null);

    void AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null);

    void AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null);

    void AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null);

    void AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null);
}
