using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistry(IOptions<McpGatewayOptions> options) : IMcpGatewayRegistry, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly McpGatewayRegistrationCollection _registrations = new(options.Value.SourceRegistrations);
    private bool _disposed;
    private int _version;

    public void AddTool(string sourceId, AITool tool, string? displayName = null)
        => Mutate(registrations => registrations.AddTool(sourceId, tool, displayName));

    public void AddTool(AITool tool, string sourceId = McpGatewayDefaults.DefaultSourceId, string? displayName = null)
        => Mutate(registrations => registrations.AddTool(tool, sourceId, displayName));

    public void AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null)
        => Mutate(registrations => registrations.AddTools(sourceId, tools, displayName));

    public void AddTools(IEnumerable<AITool> tools, string sourceId = McpGatewayDefaults.DefaultSourceId, string? displayName = null)
        => Mutate(registrations => registrations.AddTools(tools, sourceId, displayName));

    public void AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null)
        => Mutate(registrations => registrations.AddHttpServer(sourceId, endpoint, headers, displayName));

    public void AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null)
        => Mutate(registrations => registrations.AddStdioServer(sourceId, command, arguments, workingDirectory, environmentVariables, displayName));

    public void AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null)
        => Mutate(registrations => registrations.AddMcpClient(sourceId, client, disposeClient, displayName));

    public void AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null)
        => Mutate(registrations => registrations.AddMcpClientFactory(sourceId, clientFactory, disposeClient, displayName));

    public McpGatewayRegistrySnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new McpGatewayRegistrySnapshot(_version, _registrations.Snapshot());
        }
    }

    public async ValueTask DisposeAsync()
    {
        IReadOnlyList<McpGatewayToolSourceRegistration> registrations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _version++;
            registrations = _registrations.Drain();
        }

        foreach (var registration in registrations)
        {
            await registration.DisposeAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Mutate(Action<McpGatewayRegistrationCollection> mutation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            mutation(_registrations);
            _version++;
        }
    }
}
