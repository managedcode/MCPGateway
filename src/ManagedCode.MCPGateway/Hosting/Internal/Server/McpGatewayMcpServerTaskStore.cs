using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerTaskStore(IMcpGatewayTaskStore? externalStore = null) : IMcpTaskStore, IDisposable
{
    private const string TaskUnavailableMessage = "The requested task is unavailable.";
    private const string NotConfiguredMessage = "The MCP gateway task store has not been configured.";
    private IMcpGatewayTaskStore? _store;
    private IServiceProvider? _services;
    private bool _managedCatalog;

    public void ConfigureAccess(IServiceProvider services, bool managedCatalog = false)
    {
        _services = services;
        _managedCatalog = managedCatalog;
    }

    public void Configure(McpGatewayMcpTaskStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CompletedTaskRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTasks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConsecutiveStuckPolls);
        if (Volatile.Read(ref _store) is not null)
        {
            return;
        }
        var store = externalStore ?? new McpGatewayInMemoryTaskStore(options, timeProvider ?? TimeProvider.System);
        if (Interlocked.CompareExchange(ref _store, store, null) is not null && externalStore is null)
        {
            ((IDisposable)store).Dispose();
        }
    }

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
    {
        add => GetStore().InputResponseReceived += value;
        remove => GetStore().InputResponseReceived -= value;
    }

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var context = _services is null ? McpGatewayExecutionContext.TrustedLocal
            : await McpGatewayTrustedContext.ResolveAsync(_services, cancellationToken);
        if (!context.IsAuthenticated)
        {
            throw new UnauthorizedAccessException(TaskUnavailableMessage);
        }
        var frame = _services?.GetService<McpGatewayExecutionScope>()?.Current;
        return await GetStore().CreateTaskAsync(new McpGatewayTaskOwner(context.SubjectId, context.TenantId,
            context.AuthorizationPartition, context.PolicyRevision, frame?.AssetId, frame?.DefinitionHash), cancellationToken);
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        await CanAccessAsync(taskId, cancellationToken) ? await GetStore().GetTaskAsync(taskId, cancellationToken) : null;

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default) =>
        GetStore().SetCompletedAsync(taskId, result, cancellationToken);

    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default) =>
        GetStore().SetFailedAsync(taskId, error, cancellationToken);

    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(taskId, cancellationToken))
        {
            throw new UnauthorizedAccessException(TaskUnavailableMessage);
        }
        return await GetStore().SetCancelledAsync(taskId, cancellationToken);
    }

    public async Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(taskId, cancellationToken))
        {
            throw new UnauthorizedAccessException(TaskUnavailableMessage);
        }
        await GetStore().ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);
    }

    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default) =>
        GetStore().SetInputRequestsAsync(taskId, inputRequests, cancellationToken);

    public void Dispose()
    {
        if (externalStore is null && Interlocked.Exchange(ref _store, null) is IDisposable ownedStore)
        {
            ownedStore.Dispose();
        }
    }

    private async ValueTask<bool> CanAccessAsync(string taskId, CancellationToken cancellationToken)
    {
        var context = _services is null ? McpGatewayExecutionContext.TrustedLocal
            : await McpGatewayTrustedContext.ResolveAsync(_services, cancellationToken);
        if (!context.IsAuthenticated)
        {
            return false;
        }
        var owner = await GetStore().GetTaskOwnerAsync(taskId, cancellationToken);
        if (owner is null || context.SubjectId != owner.SubjectId || context.TenantId != owner.TenantId
            || context.AuthorizationPartition != owner.AuthorizationPartition || context.PolicyRevision != owner.PolicyRevision)
        {
            return false;
        }
        if (_services is null)
        {
            return true;
        }
        if (!(await McpGatewayAccessBoundary.AuthorizeAsync(_services, context,
            new(McpGatewayOperation.AccessTask, owner.AssetId ?? taskId) { DefinitionHash = owner.DefinitionHash }, cancellationToken)).IsAllowed)
        {
            return false;
        }
        if (!_managedCatalog)
        {
            return true;
        }
        var catalog = await McpGatewayTrustedContext.OperationServices(_services)
            .GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
        return owner.AssetId is not null && !catalog.RevokedToolIds.Contains(owner.AssetId)
            && catalog.Definitions.Any(definition => definition.ToolId.Equals(owner.AssetId, StringComparison.OrdinalIgnoreCase)
                && definition.DefinitionHash == owner.DefinitionHash && definition.Lifecycle == McpGatewayToolLifecycle.Published);
    }

    private IMcpGatewayTaskStore GetStore() => Volatile.Read(ref _store)
        ?? throw new InvalidOperationException(NotConfiguredMessage);
}
