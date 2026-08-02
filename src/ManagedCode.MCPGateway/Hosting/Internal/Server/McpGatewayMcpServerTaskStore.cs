using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerTaskStore : IMcpTaskStore
{
    private TaskStoreState? _state;

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
            new InMemoryMcpTaskStore
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
            state.Track(task.TaskId, task.CreatedAt + state.TimeToLive);
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

    public Task<bool> SetCancelledAsync(
        string taskId,
        CancellationToken cancellationToken = default
    ) => GetState().Store.SetCancelledAsync(taskId, cancellationToken);

    public Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default
    ) => GetState().Store.ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);

    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default
    ) => GetState().Store.SetInputRequestsAsync(taskId, inputRequests, cancellationToken);

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

    private sealed class TaskStoreState(
        InMemoryMcpTaskStore store,
        int maximumTasks,
        TimeSpan timeToLive
    )
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _expirations = new(
            StringComparer.Ordinal
        );
        private int _count;

        public InMemoryMcpTaskStore Store { get; } = store;

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

        public void Track(string taskId, DateTimeOffset expiresAt)
        {
            if (!_expirations.TryAdd(taskId, expiresAt))
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
                if (expiration.Value > now)
                {
                    continue;
                }

                _ = await Store.GetTaskAsync(expiration.Key, cancellationToken);
                Untrack(expiration.Key);
            }
        }
    }
}
