#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal abstract class TestMcpGatewayServerSource(string sourceId) : IMcpGatewayServerSource
{
    public string SourceId { get; } = sourceId;

    public virtual ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
        string toolName,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<ToolTaskSupport?>(null);

    public virtual ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<CompleteResult?>(null);

    public virtual Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ModelContextProtocol.ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<JsonElement?>(null);

    public virtual ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual Task<IAsyncDisposable?> SubscribeToResourceAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);
}

#pragma warning restore MCPEXP001
