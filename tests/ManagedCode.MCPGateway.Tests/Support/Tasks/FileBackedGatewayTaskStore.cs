using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class FileBackedGatewayTaskStore(string directory, TimeSpan initialAge) : IMcpGatewayTaskStore, IDisposable
{
    private const string TaskIdFormat = "N";
    private const string FileExtension = ".json";
    private const string PendingExtension = ".write";
    private const string MissingTaskMessage = "The persisted task is unavailable.";
    private const int SingleWriter = 1;
    private const int EmptyRequests = 0;
    private const long PollIntervalMilliseconds = 10;
    private readonly SemaphoreSlim _writer = new(SingleWriter, SingleWriter);

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;
    public TaskCompletionSource<McpTaskInfo> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CompletionAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<McpTaskInfo> CreateTaskAsync(McpGatewayTaskOwner owner, CancellationToken cancellationToken = default)
    {
        var now = TimeProvider.System.GetUtcNow();
        var task = new McpTaskInfo(Guid.CreateVersion7().ToString(TaskIdFormat), McpTaskStatus.Working,
            now - initialAge, now, null, PollIntervalMilliseconds, null, null, null, null);
        await SaveAsync(new Row(task, owner), cancellationToken);
        Created.TrySetResult(task);
        return task;
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        (await ReadAsync(taskId, cancellationToken))?.Task;

    public async Task<McpGatewayTaskOwner?> GetTaskOwnerAsync(string taskId, CancellationToken cancellationToken = default) =>
        (await ReadAsync(taskId, cancellationToken))?.Owner;

    public async Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        _ = await UpdateAsync(taskId, current => current with { Status = McpTaskStatus.Completed, Result = result.Clone() }, cancellationToken);
        CompletionAttempted.TrySetResult();
    }

    public async Task SetFailedAsync(string taskId, JsonElement failure, CancellationToken cancellationToken = default) =>
        _ = await UpdateAsync(taskId, current => current with { Status = McpTaskStatus.Failed, Error = failure.Clone() }, cancellationToken);

    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default) =>
        UpdateAsync(taskId, static current => current with { Status = McpTaskStatus.Cancelled }, cancellationToken);

    public async Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default) =>
        _ = await UpdateAsync(taskId, current => current with
        {
            Status = inputRequests.Count == EmptyRequests ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
            InputRequests = new Dictionary<string, InputRequest>(inputRequests, StringComparer.Ordinal)
        }, cancellationToken);

    public async Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        var accepted = new List<InputResponseReceivedEventArgs>();
        var changed = await UpdateAsync(taskId, current =>
        {
            var pending = current.InputRequests is null ? new Dictionary<string, InputRequest>(StringComparer.Ordinal)
                : new Dictionary<string, InputRequest>(current.InputRequests, StringComparer.Ordinal);
            foreach (var (id, response) in inputResponses)
            {
                if (pending.Remove(id))
                {
                    accepted.Add(new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = id, Response = response });
                }
            }
            return current with
            {
                Status = pending.Count == EmptyRequests ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
                InputRequests = pending
            };
        }, cancellationToken);
        if (changed)
        {
            foreach (var response in accepted)
            {
                InputResponseReceived?.Invoke(response);
            }
        }
    }

    public void Dispose() => _writer.Dispose();

    private async Task<bool> UpdateAsync(string taskId, Func<McpTaskInfo, McpTaskInfo> transform, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var row = await ReadAsync(taskId, cancellationToken) ?? throw new InvalidOperationException(MissingTaskMessage);
            if (row.Task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled)
            {
                return false;
            }
            var updated = transform(row.Task) with { LastUpdatedAt = TimeProvider.System.GetUtcNow() };
            if (updated.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled)
            {
                updated = updated with { InputRequests = null };
            }
            await SaveAsync(row with { Task = updated }, cancellationToken);
            return true;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<Row?> ReadAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(taskId, TaskIdFormat, out _))
        {
            return null;
        }
        var path = Path.Combine(directory, taskId + FileExtension);
        if (!File.Exists(path))
        {
            return null;
        }
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous
        });
        return await JsonSerializer.DeserializeAsync<Row>(file, McpGatewayJsonSerializer.Options, cancellationToken);
    }

    private async Task SaveAsync(Row row, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, row.Task.TaskId + FileExtension);
        var pending = path + Guid.CreateVersion7().ToString(TaskIdFormat) + PendingExtension;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(row, McpGatewayJsonSerializer.Options);
            await File.WriteAllBytesAsync(pending, bytes, cancellationToken);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private sealed record Row(McpTaskInfo Task, McpGatewayTaskOwner Owner);
}
