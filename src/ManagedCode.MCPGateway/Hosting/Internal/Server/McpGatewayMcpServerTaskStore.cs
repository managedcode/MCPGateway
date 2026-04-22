#pragma warning disable MCPEXP001

using System.Collections.Concurrent;
using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerTaskStore(
    IMcpGateway gateway,
    ILogger<McpGatewayMcpServerTaskStore> logger,
    ILoggerFactory loggerFactory
) : IMcpTaskStore, IAsyncDisposable
{
    private const string TaskCancelledMessage = "Task execution was cancelled.";

    private readonly InMemoryMcpTaskStore _innerStore = new();
    private readonly ConcurrentDictionary<TaskKey, TaskBinding> _bindings = new();

    public async Task<CallToolResult> CreateToolTaskAsync(
        RequestContext<CallToolRequestParams> request,
        McpGatewayResolvedToolRequest tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tool);

        var taskMetadata =
            request.Params.Task
            ?? throw new McpException(McpGatewayMcpProtocolConstants.InvalidTaskMetadataMessage);

        if (tool.TaskSupport == ToolTaskSupport.Forbidden)
        {
            throw new McpException($"Tool '{request.Params.Name}' does not support task augmentation.");
        }

        var requestId = request.JsonRpcRequest.Id;
        var sessionId = NormalizeSessionId(request.Server);

        var upstreamTask = await tool.Registration.CallToolAsTaskAsync(
            tool.ToolName,
            arguments,
            CloneTaskMetadata(taskMetadata),
            progress: null,
            loggerFactory,
            cancellationToken
        );

        if (upstreamTask is not null)
        {
            var downstreamTask = await _innerStore.CreateTaskAsync(
                CloneTaskMetadata(taskMetadata),
                requestId,
                request.JsonRpcRequest,
                sessionId,
                cancellationToken
            );

            var binding = new TaskBinding(
                TaskBindingKind.UpstreamProxy,
                sessionId,
                downstreamTask.TaskId,
                request.Server,
                tool.ToolId,
                tool.Registration,
                upstreamTask.TaskId,
                cancellationSource: null
            );

            binding.UpstreamStatusSubscription = await tool.Registration.SubscribeToTaskStatusAsync(
                upstreamTask.TaskId,
                (notification, token) => ForwardUpstreamTaskStatusAsync(binding, notification, token),
                loggerFactory,
                cancellationToken
            );

            _bindings[TaskKey.Create(sessionId, downstreamTask.TaskId)] = binding;
            return new CallToolResult { Task = CloneTask(upstreamTask, downstreamTask.TaskId) };
        }

        if (tool.TaskSupport == ToolTaskSupport.Required)
        {
            throw new McpException(
                $"Tool '{request.Params.Name}' requires task augmentation, but the upstream source does not support task-backed invocation."
            );
        }

        var localTask = await _innerStore.CreateTaskAsync(
            CloneTaskMetadata(taskMetadata),
            requestId,
            request.JsonRpcRequest,
            sessionId,
            cancellationToken
        );

        var cancellationSource = new CancellationTokenSource();
        var localBinding = new TaskBinding(
            TaskBindingKind.LocalExecution,
            sessionId,
            localTask.TaskId,
            request.Server,
            tool.ToolId,
            registration: null,
            upstreamTaskId: null,
            cancellationSource
        );

        _bindings[TaskKey.Create(sessionId, localTask.TaskId)] = localBinding;
        _ = RunLocalToolTaskAsync(localBinding, arguments);
        return new CallToolResult { Task = localTask };
    }

    public Task<McpTask> CreateTaskAsync(
        McpTaskMetadata taskParams,
        RequestId requestId,
        JsonRpcRequest request,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    ) => _innerStore.CreateTaskAsync(taskParams, requestId, request, sessionId, cancellationToken);

    public async Task<McpTask?> GetTaskAsync(
        string taskId,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;
        if (_bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding))
        {
            var proxiedTask = await TryGetProxyTaskAsync(binding, cancellationToken);
            if (proxiedTask is not null)
            {
                return proxiedTask;
            }
        }

        return await _innerStore.GetTaskAsync(taskId, sessionId, cancellationToken);
    }

    public async Task<JsonElement> GetTaskResultAsync(
        string taskId,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;

        try
        {
            if (
                _bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding)
                && binding.Kind == TaskBindingKind.UpstreamProxy
                && binding.Registration is not null
                && !string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
            )
            {
                var result = await binding.Registration.GetTaskResultAsync(
                    binding.UpstreamTaskId,
                    loggerFactory,
                    cancellationToken
                );
                if (result is { } proxiedResult)
                {
                    return proxiedResult.Clone();
                }
            }

            return await WaitForStoredTaskResultAsync(taskId, sessionId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return await WaitForStoredTaskResultAsync(taskId, sessionId, cancellationToken);
        }
    }

    public async Task<ListTasksResult> ListTasksAsync(
        string? cursor = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;
        var result = await _innerStore.ListTasksAsync(cursor, sessionId, cancellationToken);
        if (result.Tasks.Count == 0)
        {
            return result;
        }

        for (var index = 0; index < result.Tasks.Count; index++)
        {
            var task = result.Tasks[index];
            if (
                !_bindings.TryGetValue(TaskKey.Create(sessionId, task.TaskId), out var binding)
                || binding.Kind != TaskBindingKind.UpstreamProxy
            )
            {
                continue;
            }

            var proxiedTask = await TryGetProxyTaskAsync(binding, cancellationToken);
            if (proxiedTask is not null)
            {
                result.Tasks[index] = proxiedTask;
            }
        }

        return result;
    }

    public async Task<McpTask> CancelTaskAsync(
        string taskId,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;
        if (_bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding))
        {
            if (
                binding.Kind == TaskBindingKind.UpstreamProxy
                && binding.Registration is not null
                && !string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
            )
            {
                var upstreamTask =
                    await binding.Registration.CancelTaskAsync(
                        binding.UpstreamTaskId,
                        loggerFactory,
                        cancellationToken
                    )
                    ?? throw new McpException($"Task '{taskId}' could not be cancelled.");

                await UpdateTaskStatusAsync(
                    taskId,
                    upstreamTask.Status,
                    upstreamTask.StatusMessage ?? TaskCancelledMessage,
                    sessionId,
                    CancellationToken.None
                );

                return CloneTask(upstreamTask, taskId);
            }

            binding.CancellationSource?.Cancel();
            var cancelledTask = await UpdateTaskStatusAsync(
                taskId,
                McpTaskStatus.Cancelled,
                TaskCancelledMessage,
                sessionId,
                CancellationToken.None
            );

            return cancelledTask;
        }

        return await _innerStore.CancelTaskAsync(taskId, sessionId, cancellationToken);
    }

    public async Task<McpTask> StoreTaskResultAsync(
        string taskId,
        McpTaskStatus status,
        JsonElement result,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;
        var storedTask = await _innerStore.StoreTaskResultAsync(
            taskId,
            status,
            result,
            sessionId,
            cancellationToken
        );

        if (_bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding))
        {
            await NotifyTaskStatusAsync(binding.DownstreamServer, storedTask);
        }

        return storedTask;
    }

    public async Task<McpTask> UpdateTaskStatusAsync(
        string taskId,
        McpTaskStatus status,
        string? statusMessage,
        string? sessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        sessionId ??= string.Empty;
        var updatedTask = await _innerStore.UpdateTaskStatusAsync(
            taskId,
            status,
            statusMessage,
            sessionId,
            cancellationToken
        );

        if (_bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding))
        {
            await NotifyTaskStatusAsync(binding.DownstreamServer, updatedTask);
        }

        return updatedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, binding) in _bindings)
        {
            binding.CancellationSource?.Cancel();
            binding.CancellationSource?.Dispose();
            if (binding.UpstreamStatusSubscription is not null)
            {
                await binding.UpstreamStatusSubscription.DisposeAsync();
            }
        }

        _bindings.Clear();
    }

    private async Task RunLocalToolTaskAsync(
        TaskBinding binding,
        IReadOnlyDictionary<string, object?>? arguments
    )
    {
        try
        {
            var invokeResult = await gateway.InvokeAsync(
                new McpGatewayInvokeRequest(ToolId: binding.ToolId, Arguments: arguments),
                binding.CancellationSource?.Token ?? CancellationToken.None
            );

            if (binding.CancellationSource?.IsCancellationRequested == true)
            {
                return;
            }

            var toolResult = McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(invokeResult);
            var serializedResult =
                McpGatewayJsonSerializer.TrySerializeToElement(toolResult)
                ?? JsonSerializer.SerializeToElement(toolResult, McpGatewayJsonSerializer.Options);

            await StoreTaskResultAsync(
                binding.DownstreamTaskId,
                McpTaskStatus.Completed,
                serializedResult,
                binding.SessionId,
                CancellationToken.None
            );
        }
        catch (OperationCanceledException)
            when (binding.CancellationSource?.IsCancellationRequested == true)
        {
            await UpdateTaskStatusAsync(
                binding.DownstreamTaskId,
                McpTaskStatus.Cancelled,
                TaskCancelledMessage,
                binding.SessionId,
                CancellationToken.None
            );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Gateway task execution failed for tool '{ToolId}'.",
                binding.ToolId
            );

            await StoreTaskResultAsync(
                binding.DownstreamTaskId,
                McpTaskStatus.Failed,
                CreateSerializedErrorToolResult($"Task execution failed: {exception.Message}"),
                binding.SessionId,
                CancellationToken.None
            );
        }
    }

    private async ValueTask ForwardUpstreamTaskStatusAsync(
        TaskBinding binding,
        McpTaskStatusNotificationParams notification,
        CancellationToken cancellationToken
    )
    {
        var mappedTask = CloneTask(notification, binding.DownstreamTaskId);
        await UpdateTaskStatusAsync(
            binding.DownstreamTaskId,
            mappedTask.Status,
            mappedTask.StatusMessage ?? string.Empty,
            binding.SessionId,
            CancellationToken.None
        );

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<McpTask?> TryGetProxyTaskAsync(
        TaskBinding binding,
        CancellationToken cancellationToken
    )
    {
        if (
            binding.Kind != TaskBindingKind.UpstreamProxy
            || binding.Registration is null
            || string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
        )
        {
            return null;
        }

        var upstreamTask =
            await binding.Registration.GetTaskAsync(
                binding.UpstreamTaskId,
                loggerFactory,
                cancellationToken
            );
        if (upstreamTask is null)
        {
            return null;
        }

        await UpdateTaskStatusAsync(
            binding.DownstreamTaskId,
            upstreamTask.Status,
            upstreamTask.StatusMessage ?? string.Empty,
            binding.SessionId,
            CancellationToken.None
        );

        return CloneTask(upstreamTask, binding.DownstreamTaskId);
    }

    private async Task<JsonElement> WaitForStoredTaskResultAsync(
        string taskId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return await _innerStore.GetTaskResultAsync(taskId, sessionId, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                var currentTask = await _innerStore.GetTaskAsync(
                    taskId,
                    sessionId,
                    cancellationToken
                );
                if (currentTask?.Status == McpTaskStatus.Cancelled)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }

    private async Task NotifyTaskStatusAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        McpTask task
    )
    {
        if (!downstreamServer.ServerOptions.SendTaskStatusNotifications)
        {
            return;
        }

        try
        {
            await downstreamServer.NotifyTaskStatusAsync(task, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to forward MCP task status notification for task '{TaskId}'.",
                task.TaskId
            );
        }
    }

    private static string NormalizeSessionId(ModelContextProtocol.Server.McpServer server) =>
        server.SessionId ?? string.Empty;

    private static JsonElement CreateSerializedErrorToolResult(string message) =>
        McpGatewayJsonSerializer.TrySerializeToElement(
            McpGatewayMcpServerProtocolMapper.CreateErrorToolResult(message)
        )
        ?? JsonSerializer.SerializeToElement(
            McpGatewayMcpServerProtocolMapper.CreateErrorToolResult(message),
            McpGatewayJsonSerializer.Options
        );

    private static McpTaskMetadata CloneTaskMetadata(McpTaskMetadata taskMetadata) =>
        new() { TimeToLive = taskMetadata.TimeToLive };

    private static McpTask CloneTask(McpTask task, string taskId) =>
        new()
        {
            TaskId = taskId,
            Status = task.Status,
            StatusMessage = task.StatusMessage,
            CreatedAt = task.CreatedAt,
            LastUpdatedAt = task.LastUpdatedAt,
            PollInterval = task.PollInterval,
            TimeToLive = task.TimeToLive,
        };

    private static McpTask CloneTask(McpTaskStatusNotificationParams task, string taskId) =>
        new()
        {
            TaskId = taskId,
            Status = task.Status,
            StatusMessage = task.StatusMessage,
            CreatedAt = task.CreatedAt,
            LastUpdatedAt = task.LastUpdatedAt,
            PollInterval = task.PollInterval,
            TimeToLive = task.TimeToLive,
        };

    private sealed record TaskKey(string SessionId, string TaskId)
    {
        public static TaskKey Create(string sessionId, string taskId) => new(sessionId, taskId);
    }

    private sealed class TaskBinding(
        TaskBindingKind kind,
        string sessionId,
        string downstreamTaskId,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string toolId,
        McpGatewayToolSourceRegistration? registration,
        string? upstreamTaskId,
        CancellationTokenSource? cancellationSource
    )
    {
        public TaskBindingKind Kind { get; } = kind;

        public string SessionId { get; } = sessionId;

        public string DownstreamTaskId { get; } = downstreamTaskId;

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; } = downstreamServer;

        public string ToolId { get; } = toolId;

        public McpGatewayToolSourceRegistration? Registration { get; } = registration;

        public string? UpstreamTaskId { get; } = upstreamTaskId;

        public CancellationTokenSource? CancellationSource { get; } = cancellationSource;

        public IAsyncDisposable? UpstreamStatusSubscription { get; set; }
    }

    private enum TaskBindingKind
    {
        LocalExecution,
        UpstreamProxy,
    }
}

#pragma warning restore MCPEXP001
