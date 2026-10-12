using System.Runtime.ExceptionServices;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRegistry(
    IOptions<McpGatewayOptions> options,
    McpGatewayPromptChangeHub promptChangeHub
)
    : IMcpGatewayRegistry,
        IMcpGatewayCatalogRuntime,
        IMcpGatewayCatalogSource,
        IMcpGatewayCatalogStagingSource,
        IAsyncDisposable
{
    private McpGatewayRegistrationCollection _registrations = CreateRegistrations(options);
    private readonly McpGatewayOperationGate _operationGate = new();
    private int _version;
    private readonly SemaphoreSlim _reconfiguration = new(1, 1);
    private readonly System.Threading.Lock _stateGate = new();
    private Func<McpGatewayCatalogSourceSnapshot, CancellationToken, Task<McpGatewayStagedCatalog>>? _candidateValidator;

    public void SetCandidateValidator(Func<McpGatewayCatalogSourceSnapshot, CancellationToken, Task<McpGatewayStagedCatalog>> validator) =>
        _candidateValidator = validator;

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        ReconfigureAsync(new McpGatewayOptions(), cancellationToken);

    public ValueTask ReconfigureAsync(McpGatewayOptions configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ReconfigureCoreAsync(configuration.SourceRegistrations, null, null, cancellationToken);
    }

    private async ValueTask ReconfigureCoreAsync(IReadOnlyList<McpGatewayToolSourceRegistration> registrations, string? refreshSourceId,
        int? requiredVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _operationGate.Enter(this);
        var acquired = false;
        try
        {
            await _reconfiguration.WaitAsync(cancellationToken);
            acquired = true;
            var candidate = new McpGatewayRegistrationCollection(registrations);
            var committed = false;
            try
            {
                var expectedVersion = requiredVersion ?? Volatile.Read(ref _version);
                if (expectedVersion != Volatile.Read(ref _version))
                { throw new InvalidOperationException(RefreshConflictMessage); }
                var candidateSnapshot = new McpGatewayCatalogSourceSnapshot(expectedVersion + 1, candidate.Snapshot()) { Owner = candidate, RefreshSourceId = refreshSourceId };
                await using var staged = _candidateValidator is { } validate ? await validate(candidateSnapshot, cancellationToken) : null;
                cancellationToken.ThrowIfCancellationRequested();
                McpGatewayRegistrationCollection previous;
                lock (_stateGate)
                {
                    if (_version != expectedVersion)
                    {
                        throw new InvalidOperationException(RefreshConflictMessage);
                    }
                    previous = _registrations;
                    _registrations = candidate;
                    Interlocked.Increment(ref _version);
                    committed = true;
                    staged?.Publish();
                }
                promptChangeHub.NotifyChanged();
                await previous.RetireAsync();
                var cleanupExceptions = new List<Exception>();
                await DisposeRegistrationsAsync(previous.Drain().Where(source => !candidateSnapshot.Registrations.Contains(source)).ToArray(), cleanupExceptions);
                ThrowIfCleanupFailed(cleanupExceptions);
            }
            finally
            {
                if (!committed)
                {
                    await candidate.RetireAsync();
                    var retained = _registrations.Snapshot();
                    var cleanupExceptions = new List<Exception>();
                    await DisposeRegistrationsAsync(candidate.Drain().Where(source => !retained.Contains(source)).ToArray(), cleanupExceptions);
                    ThrowIfCleanupFailed(cleanupExceptions);
                }
            }
        }
        finally
        {
            _operationGate.Exit();
            if (acquired)
            {
                _reconfiguration.Release();
            }
        }
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
        AddHttpServer(
            new McpGatewayHttpServerOptions
            {
                SourceId = sourceId,
                Endpoint = endpoint,
                AdditionalHeaders = headers,
                DisplayName = displayName,
            }
        );

    public void AddHttpServer(McpGatewayHttpServerOptions httpServer) =>
        Mutate(registrations =>
            registrations.AddHttpServer(httpServer),
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
        AddStdioServer(
            new McpGatewayStdioServerOptions
            {
                SourceId = sourceId,
                Command = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = environmentVariables,
                DisplayName = displayName,
            }
        );

    public void AddStdioServer(McpGatewayStdioServerOptions stdioServer) =>
        Mutate(registrations => registrations.AddStdioServer(stdioServer),
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
            lock (_stateGate)
            {
                while (!_operationGate.IsDisposed)
                {
                    var collection = Volatile.Read(ref _registrations);
                    if (!collection.TryAcquireLease())
                    {
                        continue;
                    }
                    return new McpGatewayCatalogSourceSnapshot(Volatile.Read(ref _version), collection.Snapshot())
                    { Owner = collection, Release = collection.ReleaseLease };
                }
                throw new ObjectDisposedException(nameof(McpGatewayRegistry));
            }
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

        await _registrations.RetireAsync();
        var registrations = _registrations.Drain();
        var cleanupExceptions = new List<Exception>();
        await DisposeRegistrationsAsync(registrations, cleanupExceptions);
        _reconfiguration.Dispose();
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private static async ValueTask DisposeRegistrationsAsync(
        IReadOnlyList<McpGatewayToolSourceRegistration> registrations,
        List<Exception> cleanupExceptions
    )
    {
        foreach (var registration in registrations)
        {
            try
            {
                await registration.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }
    }

    private static void ThrowIfCleanupFailed(List<Exception> cleanupExceptions)
    {
        switch (cleanupExceptions.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                break;
            default:
                throw new AggregateException(cleanupExceptions);
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
            lock (_stateGate)
            {
                mutation(_registrations);
                Interlocked.Increment(ref _version);
            }
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
