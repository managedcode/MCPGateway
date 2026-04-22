using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistry(
    IOptions<McpGatewayOptions> options,
    McpGatewayPromptChangeHub promptChangeHub
)
    : IMcpGatewayRegistry,
        IMcpGatewayCatalogRuntime,
        IMcpGatewayCatalogSource,
        IAsyncDisposable
{
    private McpGatewayRegistrationCollection _registrations = CreateRegistrations(options);
    private readonly McpGatewayOperationGate _operationGate = new();
    private int _version;

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        ReconfigureAsync(new McpGatewayOptions(), cancellationToken);

    public async ValueTask ReconfigureAsync(
        McpGatewayOptions configuration,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<McpGatewayToolSourceRegistration> previousRegistrations;
        _operationGate.Enter(this);
        try
        {
            _operationGate.ThrowIfDisposed(this);

            var previousCollection = _registrations;
            _registrations = new McpGatewayRegistrationCollection(
                configuration.SourceRegistrations
            );
            previousRegistrations = previousCollection.Drain();
            Interlocked.Increment(ref _version);
        }
        finally
        {
            _operationGate.Exit();
        }

        foreach (var registration in previousRegistrations)
        {
            await registration.DisposeAsync();
        }

        promptChangeHub.NotifyChanged();
    }

    public void AddTool(string sourceId, AITool tool, string? displayName = null) =>
        Mutate(registrations => registrations.AddTool(sourceId, tool, displayName));

    public void AddTool(
        string sourceId,
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string? displayName = null
    ) => Mutate(registrations => registrations.AddTool(sourceId, tool, searchHints, displayName));

    public void AddTool(
        AITool tool,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) => Mutate(registrations => registrations.AddTool(tool, sourceId, displayName));

    public void AddTool(
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) => Mutate(registrations => registrations.AddTool(tool, searchHints, sourceId, displayName));

    public void AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null) =>
        Mutate(registrations => registrations.AddTools(sourceId, tools, displayName));

    public void AddTools(
        IEnumerable<AITool> tools,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) => Mutate(registrations => registrations.AddTools(tools, sourceId, displayName));

    public void AddPrompt(
        string sourceId,
        McpGatewayPrompt prompt,
        string? displayName = null
    ) =>
        Mutate(
            registrations => registrations.AddPrompt(sourceId, prompt, displayName),
            notifyPromptChanges: true
        );

    public void AddPrompt(
        McpGatewayPrompt prompt,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        Mutate(
            registrations => registrations.AddPrompt(prompt, sourceId, displayName),
            notifyPromptChanges: true
        );

    public void AddPrompts(
        string sourceId,
        IEnumerable<McpGatewayPrompt> prompts,
        string? displayName = null
    ) =>
        Mutate(
            registrations => registrations.AddPrompts(sourceId, prompts, displayName),
            notifyPromptChanges: true
        );

    public void AddPrompts(
        IEnumerable<McpGatewayPrompt> prompts,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        Mutate(
            registrations => registrations.AddPrompts(prompts, sourceId, displayName),
            notifyPromptChanges: true
        );

    public void AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null
    ) =>
        Mutate(registrations =>
            registrations.AddHttpServer(sourceId, endpoint, headers, displayName),
            notifyPromptChanges: true
        );

    public void AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null
    ) =>
        Mutate(registrations =>
            registrations.AddStdioServer(
                sourceId,
                command,
                arguments,
                workingDirectory,
                environmentVariables,
                displayName
            ),
            notifyPromptChanges: true
        );

    public void AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null
    ) =>
        Mutate(registrations =>
            registrations.AddMcpClient(sourceId, client, disposeClient, displayName),
            notifyPromptChanges: true
        );

    public void AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null
    ) =>
        Mutate(registrations =>
            registrations.AddMcpClientFactory(
                sourceId,
                clientFactory,
                disposeClient,
                displayName
            ),
            notifyPromptChanges: true
        );

    public McpGatewayCatalogSourceSnapshot CreateSnapshot()
    {
        _operationGate.Enter(this);
        try
        {
            _operationGate.ThrowIfDisposed(this);
            return new McpGatewayCatalogSourceSnapshot(
                Volatile.Read(ref _version),
                _registrations.Snapshot()
            );
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

    private void Mutate(
        Action<McpGatewayRegistrationCollection> mutation,
        bool notifyPromptChanges = false
    )
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

        if (notifyPromptChanges)
        {
            promptChangeHub.NotifyChanged();
        }
    }

    private static McpGatewayRegistrationCollection CreateRegistrations(
        IOptions<McpGatewayOptions> options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        return new McpGatewayRegistrationCollection(options.Value.SourceRegistrations);
    }
}
