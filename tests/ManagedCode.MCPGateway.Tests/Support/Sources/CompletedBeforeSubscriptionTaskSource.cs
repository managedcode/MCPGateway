#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class CompletedBeforeSubscriptionTaskSource(string sourceId)
    : IMcpGatewayServerSource
{
    private const string UpstreamTaskId = "upstream-completed-before-subscription";
    private int subscribeCount;

    public string SourceId { get; } = sourceId;

    public int SubscribeCount => Volatile.Read(ref subscribeCount);

    public ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
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

    public ValueTask<McpTask?> CallToolAsTaskAsync(
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
        return ValueTask.FromResult<McpTask?>(CreateCompletedTask(UpstreamTaskId));
    }

    public ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<McpTask?>(CreateCompletedTask(taskId));
    }

    public ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        _ = taskId;
        _ = loggerFactory;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(
                new CallToolResult { Content = [new TextContentBlock { Text = "completed" }] },
                McpJsonUtilities.DefaultOptions
            )
        );
    }

    public Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
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
        Interlocked.Increment(ref subscribeCount);
        return Task.FromResult<IAsyncDisposable?>(NoopAsyncDisposable.Instance);
    }

    public ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<CompleteResult?>(null);

    public Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<McpTask?>(null);

    public Task<IAsyncDisposable?> SubscribeToResourceAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

    private static McpTask CreateCompletedTask(string taskId)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new McpTask
        {
            TaskId = taskId,
            Status = McpTaskStatus.Completed,
            StatusMessage = "completed before downstream subscription",
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
