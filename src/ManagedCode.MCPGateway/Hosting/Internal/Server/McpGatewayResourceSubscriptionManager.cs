using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionManager(
    McpGatewayMcpServerBindingManager bindingManager,
    IServiceProvider serviceProvider,
    ILogger<McpGatewayResourceSubscriptionManager> logger,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionState> _subscriptions = new();

    public async Task SubscribeAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(exposedUri);

        var key = SubscriptionKey.Create(downstreamServer, exposedUri);
        var state = _subscriptions.GetOrAdd(key, _ => new SubscriptionState(downstreamServer));
        await state.Gate.WaitAsync(cancellationToken);

        IAsyncDisposable? previousSubscription = null;
        IAsyncDisposable? failedSubscription = null;
        var shouldPinBinding = false;
        var previousAttempt = state.ActiveAttempt;
        var attempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releasePinnedBindingAfterEarlyFailure = false;

        try
        {
            state.DownstreamServer = downstreamServer;
            shouldPinBinding = !state.HasPinnedBinding;
            state.ActiveAttempt = attempt;

            await using var bindingLease = shouldPinBinding
                ? await bindingManager.PinAsync(
                    requestServices,
                    serviceProvider,
                    downstreamServer,
                    cancellationToken
                )
                : await bindingManager.AcquireAsync(
                    requestServices,
                    serviceProvider,
                    downstreamServer,
                    cancellationToken
                );

            var resolvedRequest =
                await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
                    bindingLease.Binding,
                    exposedUri,
                    cancellationToken
                ) ?? throw new McpException($"Resource '{exposedUri}' was not found.");

            var subscription =
                await resolvedRequest.Source.SubscribeToResourceAsync(
                    resolvedRequest.UpstreamUri,
                    (notification, token) =>
                        ForwardUpdateAsync(
                            key,
                            downstreamServer,
                            resolvedRequest.ExposedUri,
                            notification,
                            attempt,
                            token
                        ),
                    loggerFactory,
                    cancellationToken
                )
                ?? throw new McpException(
                    $"Resource '{resolvedRequest.ExposedUri}' does not support subscriptions."
                );

            previousSubscription = state.Subscription;
            state.Subscription = subscription;
            if (shouldPinBinding)
            {
                state.HasPinnedBinding = true;
            }

            if (attempt.Task.IsCompletedSuccessfully)
            {
                failedSubscription = state.Subscription;
                releasePinnedBindingAfterEarlyFailure = state.HasPinnedBinding;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
            }
        }
        catch
        {
            if (ReferenceEquals(state.ActiveAttempt, attempt))
            {
                state.ActiveAttempt = previousAttempt;
            }

            if (shouldPinBinding && !state.HasPinnedBinding)
            {
                await bindingManager.ReleaseAsync(downstreamServer);
            }

            throw;
        }
        finally
        {
            state.Gate.Release();
        }

        var cleanupExceptions = new List<Exception>();
        await DisposeSubscriptionAndReleaseBindingAsync(
            failedSubscription,
            releasePinnedBindingAfterEarlyFailure,
            downstreamServer,
            cleanupExceptions
        );
        await DisposeSubscriptionAndReleaseBindingAsync(
            previousSubscription,
            releasePinnedBinding: false,
            downstreamServer,
            cleanupExceptions
        );
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    public async Task UnsubscribeAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        cancellationToken.ThrowIfCancellationRequested();

        var key = SubscriptionKey.Create(downstreamServer, exposedUri);
        if (!_subscriptions.TryGetValue(key, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);

        IAsyncDisposable? subscription;
        bool releasePinnedBinding;

        try
        {
            subscription = state.Subscription;
            releasePinnedBinding = state.HasPinnedBinding;
            state.Subscription = null;
            state.HasPinnedBinding = false;
            state.ActiveAttempt = null;
        }
        finally
        {
            state.Gate.Release();
        }

        var cleanupExceptions = new List<Exception>();
        await DisposeSubscriptionAndReleaseBindingAsync(
            subscription,
            releasePinnedBinding,
            state.DownstreamServer,
            cleanupExceptions
        );
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    public async ValueTask DisposeAsync()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        var cleanupExceptions = new List<Exception>();

        foreach (var (_, state) in subscriptions)
        {
            await state.Gate.WaitAsync(CancellationToken.None);

            IAsyncDisposable? subscription = null;
            bool releasePinnedBinding;
            var downstreamServer = state.DownstreamServer;

            try
            {
                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
            }
            finally
            {
                state.Gate.Release();
                state.Gate.Dispose();
            }

            await DisposeSubscriptionAndReleaseBindingAsync(
                subscription,
                releasePinnedBinding,
                downstreamServer,
                cleanupExceptions
            );
        }

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private async ValueTask ForwardUpdateAsync(
        SubscriptionKey key,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        ResourceUpdatedNotificationParams notification,
        TaskCompletionSource attempt,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await downstreamServer.SendNotificationAsync(
                NotificationMethods.ResourceUpdatedNotification,
                new ResourceUpdatedNotificationParams
                {
                    Uri = exposedUri,
                    Meta = notification.Meta is null ? null : (JsonObject)notification.Meta.DeepClone(),
                },
                McpJsonUtilities.DefaultOptions,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to forward MCP resource update notification for subscription '{SessionId}:{Uri}'. Removing the subscription.",
                key.SessionId,
                key.ExposedUri
            );

            attempt.TrySetResult();
            _ = RemoveFailedSubscriptionAsync(key, attempt);
        }
    }

    private async Task RemoveFailedSubscriptionAsync(
        SubscriptionKey key,
        TaskCompletionSource attempt
    )
    {
        try
        {
            if (!_subscriptions.TryGetValue(key, out var state))
            {
                return;
            }

            await state.Gate.WaitAsync(CancellationToken.None);
            IAsyncDisposable? subscription = null;
            var releasePinnedBinding = false;
            ModelContextProtocol.Server.McpServer downstreamServer;

            try
            {
                if (!ReferenceEquals(state.ActiveAttempt, attempt))
                {
                    return;
                }

                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                downstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
            }
            finally
            {
                state.Gate.Release();
            }

            var cleanupExceptions = new List<Exception>();
            await DisposeSubscriptionAndReleaseBindingAsync(
                subscription,
                releasePinnedBinding,
                downstreamServer,
                cleanupExceptions
            );
            ThrowIfCleanupFailed(cleanupExceptions);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to clean up MCP resource subscription '{SessionId}:{Uri}' after notification forwarding failed.",
                key.SessionId,
                key.ExposedUri
            );
        }
    }

    private async Task DisposeSubscriptionAndReleaseBindingAsync(
        IAsyncDisposable? subscription,
        bool releasePinnedBinding,
        ModelContextProtocol.Server.McpServer downstreamServer,
        List<Exception> cleanupExceptions
    )
    {
        if (subscription is not null)
        {
            try
            {
                await subscription.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        if (releasePinnedBinding)
        {
            try
            {
                await bindingManager.ReleaseAsync(downstreamServer);
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }
    }

    private static void ThrowIfCleanupFailed(List<Exception> cleanupExceptions)
    {
        switch (cleanupExceptions.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                break;
            default:
                throw new AggregateException(cleanupExceptions);
        }
    }

    private sealed record SubscriptionKey(string SessionId, string ExposedUri)
    {
        public static SubscriptionKey Create(
            ModelContextProtocol.Server.McpServer server,
            string exposedUri
        ) => new(server.SessionId ?? string.Empty, exposedUri);
    }

    private sealed class SubscriptionState(ModelContextProtocol.Server.McpServer downstreamServer)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } =
            downstreamServer;

        public IAsyncDisposable? Subscription { get; set; }

        public bool HasPinnedBinding { get; set; }

        public TaskCompletionSource? ActiveAttempt { get; set; }
    }
}
