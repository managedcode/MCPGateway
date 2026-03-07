using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistry(IOptions<McpGatewayOptions> options) : IMcpGatewayRegistry, IMcpGatewayCatalogSource, IAsyncDisposable
{
    private readonly McpGatewayRegistrationCollection _registrations = CreateRegistrations(options);
    private readonly McpGatewayOperationGate _operationGate = new();
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

    public McpGatewayCatalogSourceSnapshot CreateSnapshot()
    {
        _operationGate.Enter(this);
        try
        {
            _operationGate.ThrowIfDisposed(this);
            return new McpGatewayCatalogSourceSnapshot(
                Volatile.Read(ref _version),
                _registrations.Snapshot());
        }
        finally
        {
            _operationGate.Exit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_operationGate.TryStartDispose(out var waitForDrain))
        {
            return;
        }

        Interlocked.Increment(ref _version);
        await waitForDrain;

        var registrations = _registrations.Drain();
        foreach (var registration in registrations)
        {
            await registration.DisposeAsync();
        }
    }

    private void Mutate(Action<McpGatewayRegistrationCollection> mutation)
    {
        _operationGate.Enter(this);
        try
        {
            _operationGate.ThrowIfDisposed(this);
            mutation(_registrations);
            Interlocked.Increment(ref _version);
        }
        finally
        {
            _operationGate.Exit();
        }
    }

    private static McpGatewayRegistrationCollection CreateRegistrations(IOptions<McpGatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new McpGatewayRegistrationCollection(options.Value.SourceRegistrations);
    }
}
