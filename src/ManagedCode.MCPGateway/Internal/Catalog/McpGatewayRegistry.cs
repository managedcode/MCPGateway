using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistry(IOptions<McpGatewayOptions> options) : IMcpGatewayRegistry, IMcpGatewayCatalogSource, IAsyncDisposable
{
    private readonly McpGatewayRegistrationCollection _registrations = new(options.Value.SourceRegistrations);
    private int _version;
    private int _disposed;
    private int _activeOperations;
    private TaskCompletionSource<object?>? _drainSignal;

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
        EnterOperation();
        try
        {
            ThrowIfDisposed();
            return new McpGatewayCatalogSourceSnapshot(
                Volatile.Read(ref _version),
                _registrations.Snapshot());
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _version);
        var drainSignal = EnsureDrainSignal();
        if (Volatile.Read(ref _activeOperations) > 0)
        {
            await drainSignal.Task;
        }

        var registrations = _registrations.Drain();

        foreach (var registration in registrations)
        {
            await registration.DisposeAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void Mutate(Action<McpGatewayRegistrationCollection> mutation)
    {
        EnterOperation();
        try
        {
            ThrowIfDisposed();
            mutation(_registrations);
            Interlocked.Increment(ref _version);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void EnterOperation()
    {
        while (true)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref _activeOperations);
            if (Volatile.Read(ref _disposed) == 0)
            {
                return;
            }

            ExitOperation();
        }
    }

    private void ExitOperation()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0)
        {
            Volatile.Read(ref _drainSignal)?.TrySetResult(null);
        }
    }

    private TaskCompletionSource<object?> EnsureDrainSignal()
    {
        while (true)
        {
            var existing = Volatile.Read(ref _drainSignal);
            if (existing is not null)
            {
                if (Volatile.Read(ref _activeOperations) == 0)
                {
                    existing.TrySetResult(null);
                }

                return existing;
            }

            var created = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _drainSignal, created, null) is null)
            {
                if (Volatile.Read(ref _activeOperations) == 0)
                {
                    created.TrySetResult(null);
                }

                return created;
            }
        }
    }
}
