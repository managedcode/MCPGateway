#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistrationBoundServerSource(McpGatewayToolSourceRegistration registration)
    : IMcpGatewayServerSource
{
    public string SourceId { get; } = registration.SourceId;

    public async ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
        string toolName,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        var loadedTool = await registration.GetToolAsync(toolName, loggerFactory, cancellationToken);
        return loadedTool?.TaskSupport;
    }

    public ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) =>
        registration.CompleteAsync(
            reference,
            argument,
            context,
            serviceProvider,
            loggerFactory,
            cancellationToken
        );

    public Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.SubscribeToPromptListChangesAsync(onChanged, loggerFactory, cancellationToken);

    public ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) =>
        registration.CallToolAsTaskAsync(
            toolName,
            arguments,
            taskMetadata,
            progress,
            loggerFactory,
            cancellationToken
        );

    public ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.GetTaskAsync(taskId, loggerFactory, cancellationToken);

    public ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.GetTaskResultAsync(taskId, loggerFactory, cancellationToken);

    public ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.CancelTaskAsync(taskId, loggerFactory, cancellationToken);

    public Task<IAsyncDisposable?> SubscribeToResourceAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.SubscribeToResourceAsync(resourceUri, onUpdated, loggerFactory, cancellationToken);

    public Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.SubscribeToTaskStatusAsync(taskId, onUpdated, loggerFactory, cancellationToken);
}

#pragma warning restore MCPEXP001
