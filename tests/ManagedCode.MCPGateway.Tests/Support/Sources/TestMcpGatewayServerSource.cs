using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal abstract class TestMcpGatewayServerSource(string sourceId) : IMcpGatewayServerSource
{
    public string SourceId { get; } = sourceId;

    public virtual ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<CompleteResult?>(null);

    public virtual Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IAsyncDisposable?>(null);

}
