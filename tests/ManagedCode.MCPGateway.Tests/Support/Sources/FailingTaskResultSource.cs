#pragma warning disable MCPEXP001

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class FailingTaskResultSource(string sourceId) : TestMcpGatewayServerSource(sourceId)
{
    private const string UpstreamTaskId = "upstream-failing-result";

    public override ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
        string toolName,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = toolName;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ToolTaskSupport?>(ToolTaskSupport.Optional);
    }

    public override ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = toolName;
        _ = arguments;
        _ = taskMetadata;
        _ = progress;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<McpTask?>(CreateCompletedTask());
    }

    public override ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = taskId;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<McpTask?>(CreateCompletedTask());
    }

    public override ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = taskId;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        throw new McpException("upstream result failure");
    }

    public override Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = taskId;
        _ = onUpdated;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable?>(NoopAsyncDisposable.Instance);
    }

    private static McpTask CreateCompletedTask()
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new McpTask
        {
            TaskId = UpstreamTaskId,
            Status = McpTaskStatus.Completed,
            StatusMessage = "completed with unavailable result",
            CreatedAt = timestamp,
            LastUpdatedAt = timestamp,
        };
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

#pragma warning restore MCPEXP001
