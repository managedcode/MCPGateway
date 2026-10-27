using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayInMemoryTaskStore(McpGatewayMcpTaskStoreOptions options, TimeProvider timeProvider)
    : IMcpGatewayTaskStore, IDisposable
{
    private const string TaskIdFormat = "N";
    private const string MissingTaskMessage = "The requested task is unavailable.";
    private const string CapacityMessage = "The MCP task store limit of {0} tasks has been reached.";
    private static readonly CompositeFormat CapacityFormat = CompositeFormat.Parse(CapacityMessage);
    private const long EntrySize = 1;
    private const int NoRequests = 0;
    private const double ExpiredEntriesOnly = 0;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = options.MaximumTasks, Clock = new TaskClock(timeProvider) });
    private readonly int _maximumTasks = options.MaximumTasks;
    private readonly TimeSpan _retention = options.CompletedTaskRetention;
    private readonly long _pollInterval = checked((long)Math.Ceiling(options.PollInterval.TotalMilliseconds));

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    public Task<McpTaskInfo> CreateTaskAsync(McpGatewayTaskOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        _cache.Compact(ExpiredEntriesOnly);
        var now = timeProvider.GetUtcNow();
        var task = new McpTaskInfo(Guid.CreateVersion7().ToString(TaskIdFormat), McpTaskStatus.Working,
            now, now, null, _pollInterval, null, null, null, null);
        var entry = new Entry(owner, task);
        _cache.Set(task.TaskId, entry, EntryOptions());
        if (!_cache.TryGetValue<Entry>(task.TaskId, out var stored) || !ReferenceEquals(entry, stored))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, CapacityFormat, _maximumTasks));
        }
        return Task.FromResult(task);
    }

    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Read(taskId);
        return Task.FromResult(entry is null ? null : Volatile.Read(ref entry.Info));
    }

    public Task<McpGatewayTaskOwner?> GetTaskOwnerAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(taskId)?.Owner);
    }

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        _ = Update(taskId, current => current with { Status = McpTaskStatus.Completed, Result = result.Clone() }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(string taskId, JsonElement failure, CancellationToken cancellationToken = default)
    {
        _ = Update(taskId, current => current with { Status = McpTaskStatus.Failed, Error = failure.Clone() }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Update(taskId, static current => current with { Status = McpTaskStatus.Cancelled }, cancellationToken));

    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputRequests);
        var requests = new Dictionary<string, InputRequest>(StringComparer.Ordinal);
        foreach (var (id, request) in inputRequests)
        {
            requests.Add(id, new InputRequest { Method = request.Method, Params = request.Params?.Clone() });
        }
        _ = Update(taskId, current => current with
        {
            Status = requests.Count == NoRequests ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
            InputRequests = new ReadOnlyDictionary<string, InputRequest>(requests)
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputResponses);
        var accepted = new List<InputResponseReceivedEventArgs>();
        var changed = Update(taskId, current =>
        {
            accepted.Clear();
            var pending = current.InputRequests is null
                ? new Dictionary<string, InputRequest>(StringComparer.Ordinal)
                : new Dictionary<string, InputRequest>(current.InputRequests, StringComparer.Ordinal);
            foreach (var (id, response) in inputResponses)
            {
                if (pending.Remove(id))
                {
                    accepted.Add(new InputResponseReceivedEventArgs
                    {
                        TaskId = taskId,
                        RequestId = id,
                        Response = new InputResponse { RawValue = response.RawValue.Clone() }
                    });
                }
            }
            return accepted.Count == NoRequests ? current : current with
            {
                Status = pending.Count == NoRequests ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
                InputRequests = new ReadOnlyDictionary<string, InputRequest>(pending)
            };
        }, cancellationToken);
        if (changed)
        {
            foreach (var response in accepted)
            {
                InputResponseReceived?.Invoke(response);
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _cache.Dispose();

    private Entry? Read(string taskId)
    {
        if (!_cache.TryGetValue<Entry>(taskId, out var entry) || entry is null)
        {
            return null;
        }
        var info = Volatile.Read(ref entry.Info);
        if (info.TimeToLive is { } retention && timeProvider.GetUtcNow() - info.CreatedAt >= retention)
        {
            _cache.Remove(taskId);
            return null;
        }
        return entry;
    }

    private bool Update(string taskId, Func<McpTaskInfo, McpTaskInfo> transform, CancellationToken cancellationToken)
    {
        var entry = Read(taskId) ?? throw new InvalidOperationException(MissingTaskMessage);
        McpTaskInfo current;
        McpTaskInfo updated;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Volatile.Read(ref entry.Info);
            if (IsTerminal(current.Status))
            {
                return false;
            }
            updated = transform(current);
            if (ReferenceEquals(updated, current))
            {
                return false;
            }
            var now = timeProvider.GetUtcNow();
            updated = updated with { LastUpdatedAt = now };
            if (IsTerminal(updated.Status))
            {
                updated = updated with { TimeToLive = now + _retention - updated.CreatedAt, InputRequests = null };
            }
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref entry.Info, updated, current), current));

        if (IsTerminal(updated.Status))
        {
            var expiry = updated.CreatedAt + updated.TimeToLive!.Value;
            _cache.Set(taskId, entry, EntryOptions(expiry));
        }
        return true;
    }

    private static MemoryCacheEntryOptions EntryOptions(DateTimeOffset? expiry = null) => new()
    {
        Size = EntrySize,
        Priority = CacheItemPriority.NeverRemove,
        AbsoluteExpiration = expiry
    };

    private static bool IsTerminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Cancelled or McpTaskStatus.Failed;

    private sealed class TaskClock(TimeProvider provider) : ISystemClock
    {
        public DateTimeOffset UtcNow => provider.GetUtcNow();
    }

    private sealed class Entry(McpGatewayTaskOwner owner, McpTaskInfo info)
    {
        public McpGatewayTaskOwner Owner { get; } = owner;
        public McpTaskInfo Info = info;
    }
}
