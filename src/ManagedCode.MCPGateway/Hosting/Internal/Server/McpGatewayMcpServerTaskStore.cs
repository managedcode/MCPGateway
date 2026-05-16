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
    McpGatewayMcpServerBindingManager bindingManager,
    McpGatewayMcpServerRequestResolver requestResolver,
    IServiceProvider serviceProvider,
    ILogger<McpGatewayMcpServerTaskStore> logger,
    ILoggerFactory loggerFactory
) : IMcpTaskStore, IAsyncDisposable
{
    private const string TaskCancelledMessage = "Task execution was cancelled.";
    private const string TaskMessagePrefix = "Task '";
    private const string TaskNotFoundOrInaccessibleMessageSuffix =
        "' was not found or is not accessible.";
    private const string TaskCancelledWithoutResultMessageSuffix =
        "' was cancelled and has no stored result.";
    private static readonly TimeSpan TaskResultPollDelay = TimeSpan.FromMilliseconds(25);

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
        var bindingLease = !string.IsNullOrWhiteSpace(sessionId)
            ? await bindingManager.PinAsync(
                request.Services,
                serviceProvider,
                request.Server,
                cancellationToken
            )
            : await bindingManager.AcquireAsync(
                request.Services,
                serviceProvider,
                request.Server,
                cancellationToken
            );
        var bindingTransferred = false;

        try
        {
            var boundTool =
                await requestResolver.ResolveToolAsync(
                    bindingLease.Binding,
                    tool.ToolId,
                    cancellationToken
                ) ?? throw new McpException($"Tool '{tool.ToolId}' was not found.");

            var upstreamTask = await boundTool.Source.CallToolAsTaskAsync(
                boundTool.ToolName,
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
                    boundTool.ToolId,
                    boundTool.Source,
                    upstreamTask.TaskId,
                    cancellationSource: null,
                    ownedBinding: bindingLease.OwnsBinding ? bindingLease.Binding : null,
                    releasePinnedSessionBinding: !bindingLease.OwnsBinding
                );

                binding.UpstreamStatusSubscription =
                    await boundTool.Source.SubscribeToTaskStatusAsync(
                        upstreamTask.TaskId,
                        (notification, token) =>
                            ForwardUpstreamTaskStatusAsync(binding, notification, token),
                        loggerFactory,
                        cancellationToken
                    );

                bindingTransferred = true;
                _bindings[TaskKey.Create(sessionId, downstreamTask.TaskId)] = binding;
                await CatchUpProxyTaskStatusAsync(binding, cancellationToken);
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
                boundTool.ToolId,
                source: null,
                upstreamTaskId: null,
                cancellationSource,
                ownedBinding: bindingLease.OwnsBinding ? bindingLease.Binding : null,
                releasePinnedSessionBinding: !bindingLease.OwnsBinding
            );

            bindingTransferred = true;
            _bindings[TaskKey.Create(sessionId, localTask.TaskId)] = localBinding;
            _ = RunLocalToolTaskAsync(localBinding, arguments);
            return new CallToolResult { Task = localTask };
        }
        catch
        {
            if (!bindingTransferred)
            {
                if (bindingLease.OwnsBinding)
                {
                    await bindingLease.Binding.DisposeAsync();
                }
                else if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    await bindingManager.ReleaseAsync(request.Server);
                }
            }

            throw;
        }
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

        if (
            _bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding)
            && binding.Kind == TaskBindingKind.UpstreamProxy
            && binding.Source is not null
            && !string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
        )
        {
            var ownsResultMaterialization = binding.TryBeginResultFinalization();
            if (!ownsResultMaterialization)
            {
                return await WaitForStoredTaskResultAsync(taskId, sessionId, cancellationToken);
            }

            if (!binding.TryEnterOperation())
            {
                binding.ResetResultFinalization();
                return await WaitForStoredTaskResultAsync(taskId, sessionId, cancellationToken);
            }

            JsonElement? proxiedResult = null;

            try
            {
                proxiedResult = await WaitForProxyTaskResultAsync(
                    binding,
                    pollUntilAvailable: true,
                    cancellationToken
                );
            }
            finally
            {
                binding.ResetResultFinalization();
                binding.ExitOperation();
            }

            if (proxiedResult is { } clonedResult)
            {
                await StoreTaskResultCoreAsync(
                    taskId,
                    await GetTerminalStatusForStoredResultAsync(
                        binding,
                        taskId,
                        sessionId,
                        cancellationToken
                    ),
                    clonedResult,
                    sessionId,
                    notifyTaskStatus: false,
                    cancellationToken: CancellationToken.None
                );
                await ReleaseBindingAsync(binding);
                return clonedResult;
            }
        }

        return await WaitForStoredTaskResultAsync(taskId, sessionId, cancellationToken);
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
                && binding.Source is not null
                && !string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
                && binding.TryEnterOperation()
            )
            {
                McpTask? upstreamTask = null;

                try
                {
                    upstreamTask = await binding.Source.CancelTaskAsync(
                        binding.UpstreamTaskId,
                        loggerFactory,
                        cancellationToken
                    );
                }
                finally
                {
                    binding.ExitOperation();
                }

                var cancelledUpstreamTask =
                    upstreamTask ?? throw new McpException($"Task '{taskId}' could not be cancelled.");

                await UpdateTaskStatusAsync(
                    taskId,
                    cancelledUpstreamTask.Status,
                    cancelledUpstreamTask.StatusMessage ?? TaskCancelledMessage,
                    sessionId,
                    CancellationToken.None
                );

                await ReleaseBindingAsync(binding);
                return CloneTask(cancelledUpstreamTask, taskId);
            }

            binding.CancellationSource?.Cancel();
            var cancelledTask = await UpdateTaskStatusAsync(
                taskId,
                McpTaskStatus.Cancelled,
                TaskCancelledMessage,
                sessionId,
                CancellationToken.None
            );

            await ReleaseBindingAsync(binding);
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
        return await StoreTaskResultCoreAsync(
            taskId,
            status,
            result,
            sessionId,
            notifyTaskStatus: true,
            cancellationToken
        );
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
        var existingTask = await _innerStore.GetTaskAsync(taskId, sessionId, cancellationToken);
        if (existingTask is not null && IsTerminal(existingTask.Status))
        {
            return existingTask;
        }

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
        foreach (var (_, binding) in _bindings.ToArray())
        {
            await ReleaseBindingAsync(binding);
        }
    }

    private async Task RunLocalToolTaskAsync(
        TaskBinding binding,
        IReadOnlyDictionary<string, object?>? arguments
    )
    {
        if (!binding.TryEnterOperation())
        {
            return;
        }

        JsonElement? storedResult = null;
        McpTaskStatus? finalStatus = null;
        string? finalStatusMessage = null;

        try
        {
            await using var bindingLease = binding.OwnedBinding is null
                ? await bindingManager.AcquireAsync(
                    requestServices: null,
                    serviceProvider,
                    binding.DownstreamServer,
                    binding.CancellationSource?.Token ?? CancellationToken.None
                )
                : default;

            var gateway = binding.OwnedBinding ?? bindingLease.Binding;
            var invokeResult = await gateway.Gateway.InvokeAsync(
                new McpGatewayInvokeRequest(ToolId: binding.ToolId, Arguments: arguments),
                binding.CancellationSource?.Token ?? CancellationToken.None
            );

            if (binding.CancellationSource?.IsCancellationRequested == true)
            {
                return;
            }

            var toolResult = McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(invokeResult);
            storedResult =
                McpGatewayJsonSerializer.TrySerializeToElement(toolResult)
                ?? JsonSerializer.SerializeToElement(toolResult, McpGatewayJsonSerializer.Options);
            finalStatus = McpTaskStatus.Completed;
        }
        catch (OperationCanceledException)
            when (binding.CancellationSource?.IsCancellationRequested == true)
        {
            finalStatus = McpTaskStatus.Cancelled;
            finalStatusMessage = TaskCancelledMessage;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Gateway task execution failed for tool '{ToolId}'.",
                binding.ToolId
            );

            storedResult = CreateSerializedErrorToolResult($"Task execution failed: {exception.Message}");
            finalStatus = McpTaskStatus.Failed;
        }
        finally
        {
            binding.ExitOperation();
        }

        switch (finalStatus)
        {
            case McpTaskStatus.Completed:
            case McpTaskStatus.Failed:
                await StoreTaskResultAsync(
                    binding.DownstreamTaskId,
                    finalStatus.Value,
                    storedResult ?? default,
                    binding.SessionId,
                    CancellationToken.None
                );
                await ReleaseBindingAsync(binding);
                break;
            case McpTaskStatus.Cancelled:
                await UpdateTaskStatusAsync(
                    binding.DownstreamTaskId,
                    McpTaskStatus.Cancelled,
                    finalStatusMessage ?? TaskCancelledMessage,
                    binding.SessionId,
                    CancellationToken.None
                );
                await ReleaseBindingAsync(binding);
                break;
        }
    }

    private async ValueTask ForwardUpstreamTaskStatusAsync(
        TaskBinding binding,
        McpTaskStatusNotificationParams notification,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mappedTask = CloneTask(notification, binding.DownstreamTaskId);
        var shouldReleaseBinding = false;
        var shouldScheduleFinalization = false;

        switch (mappedTask.Status)
        {
            case McpTaskStatus.Completed:
            case McpTaskStatus.Failed:
                binding.RememberStoredResultStatus(mappedTask.Status);
                if (binding.TryMarkTerminalStatusForwarded())
                {
                    await NotifyTaskStatusAsync(binding.DownstreamServer, mappedTask);
                }

                shouldScheduleFinalization = true;
                break;
            case McpTaskStatus.Cancelled:
                if (binding.TryMarkTerminalStatusForwarded())
                {
                    await UpdateTaskStatusAsync(
                        binding.DownstreamTaskId,
                        mappedTask.Status,
                        mappedTask.StatusMessage ?? string.Empty,
                        binding.SessionId,
                        CancellationToken.None
                    );
                }

                shouldReleaseBinding = true;
                break;
            default:
                await UpdateTaskStatusAsync(
                    binding.DownstreamTaskId,
                    mappedTask.Status,
                    mappedTask.StatusMessage ?? string.Empty,
                    binding.SessionId,
                    CancellationToken.None
                );
                break;
        }

        if (shouldReleaseBinding)
        {
            await ReleaseBindingAsync(binding);
        }
        else if (shouldScheduleFinalization)
        {
            ScheduleProxyTaskFinalization(binding, mappedTask.Status);
        }
    }

    private async ValueTask CatchUpProxyTaskStatusAsync(
        TaskBinding binding,
        CancellationToken cancellationToken
    )
    {
        if (
            binding.Kind != TaskBindingKind.UpstreamProxy
            || binding.Source is null
            || string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
            || !binding.TryEnterOperation()
        )
        {
            return;
        }

        McpTask? upstreamTask = null;
        var shouldReleaseBinding = false;
        var shouldScheduleFinalization = false;

        try
        {
            try
            {
                upstreamTask = await binding.Source.GetTaskAsync(
                    binding.UpstreamTaskId,
                    loggerFactory,
                    cancellationToken
                );
            }
            catch (InvalidOperationException exception)
            {
                LogUpstreamTaskStatusReadFailed(binding, exception);
                return;
            }
            catch (McpProtocolException exception)
            {
                LogUpstreamTaskStatusReadFailed(binding, exception);
                return;
            }

            if (upstreamTask is null)
            {
                return;
            }

            var mappedTask = CloneTask(upstreamTask, binding.DownstreamTaskId);
            switch (mappedTask.Status)
            {
                case McpTaskStatus.Completed:
                case McpTaskStatus.Failed:
                    binding.RememberStoredResultStatus(mappedTask.Status);
                    if (binding.TryMarkTerminalStatusForwarded())
                    {
                        await NotifyTaskStatusAsync(binding.DownstreamServer, mappedTask);
                    }

                    shouldScheduleFinalization = true;
                    break;
                case McpTaskStatus.Cancelled:
                    if (binding.TryMarkTerminalStatusForwarded())
                    {
                        await UpdateTaskStatusAsync(
                            binding.DownstreamTaskId,
                            mappedTask.Status,
                            mappedTask.StatusMessage ?? string.Empty,
                            binding.SessionId,
                            CancellationToken.None
                        );
                    }

                    shouldReleaseBinding = true;
                    break;
                default:
                    await UpdateTaskStatusAsync(
                        binding.DownstreamTaskId,
                        mappedTask.Status,
                        mappedTask.StatusMessage ?? string.Empty,
                        binding.SessionId,
                        CancellationToken.None
                    );
                    break;
            }
        }
        finally
        {
            binding.ExitOperation();

            if (shouldReleaseBinding)
            {
                await ReleaseBindingAsync(binding);
            }
            else if (shouldScheduleFinalization)
            {
                ScheduleProxyTaskFinalization(
                    binding,
                    upstreamTask?.Status ?? McpTaskStatus.Completed
                );
            }
        }
    }

    private async Task<McpTask> StoreTaskResultCoreAsync(
        string taskId,
        McpTaskStatus status,
        JsonElement result,
        string sessionId,
        bool notifyTaskStatus,
        CancellationToken cancellationToken
    )
    {
        var storedTask = await _innerStore.StoreTaskResultAsync(
            taskId,
            status,
            result,
            sessionId,
            cancellationToken
        );

        if (
            notifyTaskStatus
            && _bindings.TryGetValue(TaskKey.Create(sessionId, taskId), out var binding)
        )
        {
            await NotifyTaskStatusAsync(binding.DownstreamServer, storedTask);
        }

        return storedTask;
    }

    private async Task<McpTaskStatus> GetTerminalStatusForStoredResultAsync(
        TaskBinding binding,
        string taskId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        var storedResultStatus = binding.GetStoredResultStatus();
        if (storedResultStatus is not null)
        {
            return storedResultStatus.Value;
        }

        var task = await _innerStore.GetTaskAsync(taskId, sessionId, cancellationToken);
        return task is not null && task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed
            ? task.Status
            : McpTaskStatus.Completed;
    }

    private async Task ReleaseBindingAsync(TaskBinding binding)
    {
        if (!binding.TryBeginRelease())
        {
            return;
        }

        _bindings.TryRemove(TaskKey.Create(binding.SessionId, binding.DownstreamTaskId), out _);
        await binding.WaitForOperationsToCompleteAsync();

        binding.CancellationSource?.Cancel();
        binding.CancellationSource?.Dispose();

        if (binding.UpstreamStatusSubscription is not null)
        {
            await binding.UpstreamStatusSubscription.DisposeAsync();
        }

        if (binding.ReleasePinnedSessionBinding)
        {
            await bindingManager.ReleaseAsync(binding.DownstreamServer);
        }

        if (binding.OwnedBinding is not null)
        {
            await binding.OwnedBinding.DisposeAsync();
        }
    }

    private void ScheduleProxyTaskFinalization(
        TaskBinding binding,
        McpTaskStatus terminalStatus
    )
    {
        if (!binding.TryBeginResultFinalization())
        {
            return;
        }

        _ = FinalizeProxyTaskResultAsync(binding, terminalStatus);
    }

    private async Task FinalizeProxyTaskResultAsync(
        TaskBinding binding,
        McpTaskStatus terminalStatus
    )
    {
        if (
            binding.Kind != TaskBindingKind.UpstreamProxy
            || binding.Source is null
            || string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
        )
        {
            binding.ResetResultFinalization();
            return;
        }

        if (!binding.TryEnterOperation())
        {
            binding.ResetResultFinalization();
            return;
        }

        var shouldReleaseBinding = false;

        try
        {
            var upstreamResult = await TryGetProxyTaskResultAsync(binding, CancellationToken.None);
            if (upstreamResult is null)
            {
                logger.LogDebug(
                    "Proxy task result for task '{TaskId}' was not available during background finalization.",
                    binding.DownstreamTaskId
                );
                binding.ResetResultFinalization();
                return;
            }

            await StoreTaskResultCoreAsync(
                binding.DownstreamTaskId,
                terminalStatus,
                upstreamResult.Value,
                binding.SessionId,
                notifyTaskStatus: false,
                cancellationToken: CancellationToken.None
            );
            shouldReleaseBinding = true;
            binding.ResetResultFinalization();
        }
        catch (Exception exception)
        {
            binding.ResetResultFinalization();
            logger.LogDebug(
                exception,
                "Failed to finalize proxy task result for task '{TaskId}'.",
                binding.DownstreamTaskId
            );
        }
        finally
        {
            binding.ExitOperation();
        }

        if (shouldReleaseBinding)
        {
            await ReleaseBindingAsync(binding);
        }
    }

    private async Task<McpTask?> TryGetProxyTaskAsync(
        TaskBinding binding,
        CancellationToken cancellationToken
    )
    {
        if (
            binding.Kind != TaskBindingKind.UpstreamProxy
            || binding.Source is null
            || string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
            || !binding.TryEnterOperation()
        )
        {
            return null;
        }

        McpTask? upstreamTask = null;
        var shouldReleaseBinding = false;
        var shouldScheduleFinalization = false;

        try
        {
            try
            {
                upstreamTask = await binding.Source.GetTaskAsync(
                    binding.UpstreamTaskId,
                    loggerFactory,
                    cancellationToken
                );
            }
            catch (InvalidOperationException exception)
            {
                LogUpstreamTaskStatusReadFailed(binding, exception);
                return null;
            }
            catch (McpProtocolException exception)
            {
                LogUpstreamTaskStatusReadFailed(binding, exception);
                return null;
            }

            if (upstreamTask is null)
            {
                return null;
            }

            switch (upstreamTask.Status)
            {
                case McpTaskStatus.Completed:
                case McpTaskStatus.Failed:
                    binding.RememberStoredResultStatus(upstreamTask.Status);
                    shouldScheduleFinalization = true;
                    break;
                case McpTaskStatus.Cancelled:
                    await UpdateTaskStatusAsync(
                        binding.DownstreamTaskId,
                        upstreamTask.Status,
                        upstreamTask.StatusMessage ?? string.Empty,
                        binding.SessionId,
                        CancellationToken.None
                    );
                    shouldReleaseBinding = true;
                    break;
                default:
                    await UpdateTaskStatusAsync(
                        binding.DownstreamTaskId,
                        upstreamTask.Status,
                        upstreamTask.StatusMessage ?? string.Empty,
                        binding.SessionId,
                        CancellationToken.None
                    );
                    break;
            }

            return CloneTask(upstreamTask, binding.DownstreamTaskId);
        }
        finally
        {
            binding.ExitOperation();

            if (shouldReleaseBinding)
            {
                await ReleaseBindingAsync(binding);
            }
            else if (shouldScheduleFinalization)
            {
                ScheduleProxyTaskFinalization(
                    binding,
                    upstreamTask?.Status ?? McpTaskStatus.Completed
                );
            }
        }
    }

    private async Task<JsonElement> WaitForStoredTaskResultAsync(
        string taskId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentTask = await _innerStore.GetTaskAsync(taskId, sessionId, cancellationToken);
            switch (currentTask?.Status)
            {
                case null:
                    throw new InvalidOperationException(
                        CreateTaskMessage(taskId, TaskNotFoundOrInaccessibleMessageSuffix)
                    );
                case McpTaskStatus.Completed:
                case McpTaskStatus.Failed:
                    return await _innerStore.GetTaskResultAsync(taskId, sessionId, cancellationToken);
                case McpTaskStatus.Cancelled:
                    throw new InvalidOperationException(
                        CreateTaskMessage(taskId, TaskCancelledWithoutResultMessageSuffix)
                    );
                default:
                    await Task.Delay(TaskResultPollDelay, cancellationToken);
                    break;
            }
        }
    }

    private static string CreateTaskMessage(string taskId, string suffix) =>
        string.Concat(TaskMessagePrefix, taskId, suffix);

    private async Task<JsonElement?> WaitForProxyTaskResultAsync(
        TaskBinding binding,
        bool pollUntilAvailable,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryGetProxyTaskResultAsync(binding, cancellationToken);
            if (result is { } proxiedResult)
            {
                return proxiedResult;
            }

            if (!pollUntilAvailable)
            {
                return null;
            }

            await Task.Delay(TaskResultPollDelay, cancellationToken);
        }
    }

    private async Task<JsonElement?> TryGetProxyTaskResultAsync(
        TaskBinding binding,
        CancellationToken cancellationToken
    )
    {
        if (
            binding.Source is null
            || string.IsNullOrWhiteSpace(binding.UpstreamTaskId)
        )
        {
            return null;
        }

        var result = await binding.Source.GetTaskResultAsync(
            binding.UpstreamTaskId,
            loggerFactory,
            cancellationToken
        );
        return result?.Clone();
    }

    private void LogUpstreamTaskStatusReadFailed(TaskBinding binding, Exception exception)
    {
        logger.LogDebug(
            exception,
            "Failed to read upstream MCP task status for downstream task '{TaskId}'.",
            binding.DownstreamTaskId
        );
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

    private static bool IsTerminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

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
        IMcpGatewayServerSource? source,
        string? upstreamTaskId,
        CancellationTokenSource? cancellationSource,
        IMcpGatewayServerBinding? ownedBinding,
        bool releasePinnedSessionBinding
    )
    {
        public TaskBindingKind Kind { get; } = kind;

        public string SessionId { get; } = sessionId;

        public string DownstreamTaskId { get; } = downstreamTaskId;

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; } = downstreamServer;

        public string ToolId { get; } = toolId;

        public IMcpGatewayServerSource? Source { get; } = source;

        public string? UpstreamTaskId { get; } = upstreamTaskId;

        public CancellationTokenSource? CancellationSource { get; } = cancellationSource;

        public IMcpGatewayServerBinding? OwnedBinding { get; } = ownedBinding;

        public bool ReleasePinnedSessionBinding { get; } = releasePinnedSessionBinding;

        public IAsyncDisposable? UpstreamStatusSubscription { get; set; }

        private int _activeOperations;
        private int _resultFinalizationState;
        private int _terminalStatusForwarded;
        private int _storedResultStatus = -1;

        private readonly TaskCompletionSource<object?> _operationsCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int ReleaseState;

        public bool TryEnterOperation()
        {
            while (true)
            {
                if (Volatile.Read(ref ReleaseState) != 0)
                {
                    return false;
                }

                var activeOperations = Volatile.Read(ref _activeOperations);
                if (
                    Interlocked.CompareExchange(
                        ref _activeOperations,
                        activeOperations + 1,
                        activeOperations
                    ) != activeOperations
                )
                {
                    continue;
                }

                if (Volatile.Read(ref ReleaseState) == 0)
                {
                    return true;
                }

                ExitOperation();
                return false;
            }
        }

        public void ExitOperation()
        {
            if (Interlocked.Decrement(ref _activeOperations) == 0 && Volatile.Read(ref ReleaseState) != 0)
            {
                _operationsCompleted.TrySetResult(null);
            }
        }

        public bool TryBeginRelease()
        {
            if (Interlocked.Exchange(ref ReleaseState, 1) != 0)
            {
                return false;
            }

            if (Volatile.Read(ref _activeOperations) == 0)
            {
                _operationsCompleted.TrySetResult(null);
            }

            return true;
        }

        public Task WaitForOperationsToCompleteAsync() => _operationsCompleted.Task;

        public bool TryBeginResultFinalization() =>
            Interlocked.CompareExchange(ref _resultFinalizationState, 1, 0) == 0;

        public void ResetResultFinalization() => Interlocked.Exchange(ref _resultFinalizationState, 0);

        public bool TryMarkTerminalStatusForwarded() =>
            Interlocked.CompareExchange(ref _terminalStatusForwarded, 1, 0) == 0;

        public void RememberStoredResultStatus(McpTaskStatus status)
        {
            if (status is McpTaskStatus.Completed or McpTaskStatus.Failed)
            {
                Volatile.Write(ref _storedResultStatus, (int)status);
            }
        }

        public McpTaskStatus? GetStoredResultStatus()
        {
            var storedResultStatus = Volatile.Read(ref _storedResultStatus);
            return storedResultStatus >= 0 ? (McpTaskStatus)storedResultStatus : null;
        }
    }

    private enum TaskBindingKind
    {
        LocalExecution,
        UpstreamProxy,
    }
}

#pragma warning restore MCPEXP001
