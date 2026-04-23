#pragma warning disable MCPEXP001

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayServerSource
{
    string SourceId { get; }

    ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
        string toolName,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    Task<IAsyncDisposable?> SubscribeToResourceAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );
}

#pragma warning restore MCPEXP001
