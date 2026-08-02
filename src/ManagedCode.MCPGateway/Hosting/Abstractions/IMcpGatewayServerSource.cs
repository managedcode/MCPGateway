using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayServerSource
{
    string SourceId { get; }

    ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

    Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    );

}
