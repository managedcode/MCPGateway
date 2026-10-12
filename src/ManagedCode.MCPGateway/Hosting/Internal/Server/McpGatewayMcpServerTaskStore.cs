using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerTaskStore(IMcpTaskStore? externalStore = null) : IMcpTaskStore
{
    private const string TaskUnavailableMessage = "The requested task is unavailable.";
    private TaskStoreState? _state;
    private IServiceProvider? _services;
    private bool _managedCatalog;
    public void ConfigureAccess(IServiceProvider services, bool managedCatalog = false)
    {
        _services = services;
        _managedCatalog = managedCatalog;
    }

    internal int Count => Volatile.Read(ref _state)?.Count ?? 0;

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
    {
        add => GetState().Store.InputResponseReceived += value;
        remove => GetState().Store.InputResponseReceived -= value;
    }

    public void Configure(McpGatewayMcpTaskStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidatePositive(options.TaskTimeToLive, nameof(options.TaskTimeToLive));
        ValidatePositive(options.PollInterval, nameof(options.PollInterval));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTasks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumConsecutiveStuckPolls
        );

        var pollIntervalMilliseconds = checked((long)Math.Ceiling(options.PollInterval.TotalMilliseconds));
        var state = new TaskStoreState(
            externalStore ?? new InMemoryMcpTaskStore
            {
                DefaultTimeToLive = options.TaskTimeToLive,
                DefaultPollIntervalMs = pollIntervalMilliseconds,
            },
            options.MaximumTasks,
            options.TaskTimeToLive
        );

        _ = Interlocked.CompareExchange(ref _state, state, null);
    }

    public async Task<McpTaskInfo> CreateTaskAsync(
        CancellationToken cancellationToken = default
    )
    {
        var state = GetState();
        await state.SweepExpiredAsync(cancellationToken);
        state.ReserveSlot();

        try
        {
            var task = await state.Store.CreateTaskAsync(cancellationToken);
            var owner = _services is null ? McpGatewayExecutionContext.TrustedLocal : await McpGatewayTrustedContext.ResolveAsync(_services, cancellationToken);
            var frame = _services?.GetService<McpGatewayExecutionScope>()?.Current;
            state.Track(task.TaskId, task.CreatedAt + state.TimeToLive, owner, frame?.AssetId, frame?.DefinitionHash);
            return task;
        }
        catch
        {
            state.ReleaseSlot();
            throw;
        }
    }

    public async Task<McpTaskInfo?> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default
    )
    {
        var state = GetState();
        if (!await CanAccessAsync(taskId, cancellationToken))
        {
            return null;
        }
        var task = await state.Store.GetTaskAsync(taskId, cancellationToken);
        if (task is null)
        {
            state.Untrack(taskId);
        }

        return task;
    }

    public Task SetCompletedAsync(
        string taskId,
        JsonElement result,
        CancellationToken cancellationToken = default
    ) => GetState().Store.SetCompletedAsync(taskId, result, cancellationToken);

    public Task SetFailedAsync(
        string taskId,
        JsonElement error,
        CancellationToken cancellationToken = default
    ) => GetState().Store.SetFailedAsync(taskId, error, cancellationToken);

    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(taskId, cancellationToken))
        { throw new UnauthorizedAccessException(TaskUnavailableMessage); }
        return await GetState().Store.SetCancelledAsync(taskId, cancellationToken);
    }

    public async Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(taskId, cancellationToken))
        {
            throw new UnauthorizedAccessException(TaskUnavailableMessage);
        }
        await GetState().Store.ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);
    }

    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default
    ) => GetState().Store.SetInputRequestsAsync(taskId, inputRequests, cancellationToken);

    private async ValueTask<bool> CanAccessAsync(string taskId, CancellationToken cancellationToken)
    {
        if (_services is null)
        {
            return true;
        }
        var context = await McpGatewayTrustedContext.ResolveAsync(_services, cancellationToken);
        if (!GetState().TryGetOwner(taskId, out var owner) || !context.IsAuthenticated
            || context.SubjectId != owner.Context.SubjectId || context.TenantId != owner.Context.TenantId
            || context.AuthorizationPartition != owner.Context.AuthorizationPartition || context.PolicyRevision != owner.Context.PolicyRevision)
        {
            return false;
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
        var catalog = await McpGatewayTrustedContext.OperationServices(_services).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
        return owner.AssetId is not null && !catalog.RevokedToolIds.Contains(owner.AssetId)
            && catalog.Definitions.Any(definition => definition.ToolId.Equals(owner.AssetId, StringComparison.OrdinalIgnoreCase)
                && definition.DefinitionHash == owner.DefinitionHash && definition.Lifecycle == McpGatewayToolLifecycle.Published);
    }

    private TaskStoreState GetState() =>
        Volatile.Read(ref _state)
        ?? throw new InvalidOperationException(
            $"{nameof(McpGatewayMcpServerTaskStore)} has not been configured."
        );

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be positive.");
        }
    }

    private sealed record TaskOwner(DateTimeOffset ExpiresAt, McpGatewayExecutionContext Context, string? AssetId, string? DefinitionHash);

    private sealed class TaskStoreState(
        IMcpTaskStore store,
        int maximumTasks,
        TimeSpan timeToLive
    )
    {
        private readonly ConcurrentDictionary<string, TaskOwner> _expirations = new(
            StringComparer.Ordinal
        );
        private int _count;

        public IMcpTaskStore Store { get; } = store;

        public TimeSpan TimeToLive { get; } = timeToLive;

        public int Count => Volatile.Read(ref _count);

        public void ReserveSlot()
        {
            var count = Volatile.Read(ref _count);
            while (count < maximumTasks)
            {
                var reserved = Interlocked.CompareExchange(ref _count, count + 1, count);
                if (reserved == count)
                {
                    return;
                }

                count = reserved;
            }

            throw new InvalidOperationException(
                $"The MCP task store limit of {maximumTasks} tasks has been reached."
            );
        }

        public bool TryGetOwner(string taskId, out TaskOwner owner) => _expirations.TryGetValue(taskId, out owner!);

        public void Track(string taskId, DateTimeOffset expiresAt, McpGatewayExecutionContext context, string? assetId, string? definitionHash)
        {
            if (!_expirations.TryAdd(taskId, new(expiresAt, context, assetId, definitionHash)))
            {
                throw new InvalidOperationException($"Task '{taskId}' is already tracked.");
            }
        }

        public void Untrack(string taskId)
        {
            if (_expirations.TryRemove(taskId, out _))
            {
                ReleaseSlot();
            }
        }

        public void ReleaseSlot() => Interlocked.Decrement(ref _count);

        public async Task SweepExpiredAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expiration in _expirations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expiration.Value.ExpiresAt > now)
                {
                    continue;
                }

                _ = await Store.GetTaskAsync(expiration.Key, cancellationToken);
                Untrack(expiration.Key);
            }
        }
    }
}
