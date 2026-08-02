using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistrationBoundServerSource(McpGatewayToolSourceRegistration registration)
    : IMcpGatewayServerSource
{
    public string SourceId { get; } = registration.SourceId;

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

    public Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.ListenForPromptListChangesAsync(onChanged, loggerFactory, cancellationToken);

    public Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    ) => registration.ListenForResourceUpdatesAsync(resourceUri, onUpdated, loggerFactory, cancellationToken);

}
