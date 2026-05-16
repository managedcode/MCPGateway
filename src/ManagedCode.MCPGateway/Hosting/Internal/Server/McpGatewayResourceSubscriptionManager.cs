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
    private int _disposed;

    internal int SubscriptionStateCount => _subscriptions.Count;

    public async Task SubscribeAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(exposedUri);
        ThrowIfDisposed();

        var key = SubscriptionKey.Create(downstreamServer, exposedUri);

        while (true)
        {
            var state = GetOrAddActiveState(key, downstreamServer);
            IAsyncDisposable? previousSubscription = null;
            IAsyncDisposable? failedSubscription = null;
            var shouldPinBinding = false;
            var previousAttempt = state.ActiveAttempt;
            var attempt = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var releasePinnedBindingAfterEarlyFailure = false;

            await state.Gate.WaitAsync(cancellationToken);

            try
            {
                if (state.IsRetired)
                {
                    continue;
                }

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
                    TryRemoveInactiveState(key, state);
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

                TryRemoveInactiveState(key, state);

                throw;
            }
            finally
            {
                state.Gate.Release();
                ReleaseStateReference(state);
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
            return;
        }
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
        if (!TryGetActiveState(key, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);

        IAsyncDisposable? subscription;
        var releasePinnedBinding = false;
        var retiredState = false;
        var cleanupDownstreamServer = state.DownstreamServer;

        try
        {
            if (state.IsRetired)
            {
                subscription = null;
            }
            else
            {
                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                cleanupDownstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                retiredState = TryRemoveInactiveState(key, state);
            }
        }
        finally
        {
            state.Gate.Release();
            ReleaseStateReference(state);
        }

        if (retiredState)
        {
            logger.LogDebug(
                "Removed MCP resource subscription state '{SessionId}:{Uri}' after unsubscribe.",
                key.SessionId,
                key.ExposedUri
            );
        }

        var cleanupExceptions = new List<Exception>();
        await DisposeSubscriptionAndReleaseBindingAsync(
            subscription,
            releasePinnedBinding,
            cleanupDownstreamServer,
            cleanupExceptions
        );
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    internal async ValueTask RemoveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var subscriptions = _subscriptions
            .Where(subscription => string.Equals(subscription.Key.SessionId, sessionId, StringComparison.Ordinal))
            .ToArray();
        var cleanupExceptions = new List<Exception>();

        foreach (var (key, state) in subscriptions)
        {
            if (!state.TryRetain())
            {
                continue;
            }

            await state.Gate.WaitAsync(cancellationToken);

            IAsyncDisposable? subscription = null;
            bool releasePinnedBinding;
            var downstreamServer = state.DownstreamServer;
            var removedState = false;

            try
            {
                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                downstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                state.MarkRetired();
                removedState = _subscriptions.TryRemove(
                    new KeyValuePair<SubscriptionKey, SubscriptionState>(key, state)
                );
            }
            finally
            {
                state.Gate.Release();
                ReleaseStateReference(state);
            }

            if (removedState)
            {
                logger.LogDebug(
                    "Removed MCP resource subscription state '{SessionId}:{Uri}' after session cleanup.",
                    key.SessionId,
                    key.ExposedUri
                );
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        var cleanupExceptions = new List<Exception>();

        foreach (var (_, state) in subscriptions)
        {
            if (!state.TryRetain())
            {
                continue;
            }

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
                state.MarkRetired();
            }
            finally
            {
                state.Gate.Release();
                ReleaseStateReference(state);
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
            if (!TryGetActiveState(key, out var state))
            {
                return;
            }

            await state.Gate.WaitAsync(CancellationToken.None);
            IAsyncDisposable? subscription = null;
            var releasePinnedBinding = false;
            var downstreamServer = state.DownstreamServer;
            var shouldCleanUp = false;

            try
            {
                if (state.IsRetired || !ReferenceEquals(state.ActiveAttempt, attempt))
                {
                    return;
                }

                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                downstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                TryRemoveInactiveState(key, state);
                shouldCleanUp = true;
            }
            finally
            {
                state.Gate.Release();
                ReleaseStateReference(state);
            }

            if (!shouldCleanUp)
            {
                return;
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

    private SubscriptionState GetOrAddActiveState(
        SubscriptionKey key,
        ModelContextProtocol.Server.McpServer downstreamServer
    )
    {
        while (true)
        {
            ThrowIfDisposed();

            if (_subscriptions.TryGetValue(key, out var existingState))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    ThrowIfDisposed();
                }

                if (existingState.TryRetain())
                {
                    return existingState;
                }

                _subscriptions.TryRemove(
                    new KeyValuePair<SubscriptionKey, SubscriptionState>(key, existingState)
                );
                continue;
            }

            var createdState = new SubscriptionState(downstreamServer);
            if (!_subscriptions.TryAdd(key, createdState))
            {
                createdState.DisposeGate();
                continue;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                createdState.MarkRetired();
                _subscriptions.TryRemove(
                    new KeyValuePair<SubscriptionKey, SubscriptionState>(key, createdState)
                );
                createdState.DisposeGate();
                ThrowIfDisposed();
            }

            if (createdState.TryRetain())
            {
                return createdState;
            }

            _subscriptions.TryRemove(
                new KeyValuePair<SubscriptionKey, SubscriptionState>(key, createdState)
            );
        }
    }

    private bool TryGetActiveState(
        SubscriptionKey key,
        out SubscriptionState state
    )
    {
        while (_subscriptions.TryGetValue(key, out var existingState))
        {
            if (existingState.TryRetain())
            {
                state = existingState;
                return true;
            }

            _subscriptions.TryRemove(
                new KeyValuePair<SubscriptionKey, SubscriptionState>(key, existingState)
            );
        }

        state = null!;
        return false;
    }

    private bool TryRemoveInactiveState(SubscriptionKey key, SubscriptionState state)
    {
        if (!state.IsInactive)
        {
            return false;
        }

        state.MarkRetired();
        _subscriptions.TryRemove(new KeyValuePair<SubscriptionKey, SubscriptionState>(key, state));
        return true;
    }

    private static void ReleaseStateReference(SubscriptionState state)
    {
        if (state.ReleaseReference())
        {
            state.Gate.Dispose();
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
        private readonly object _sync = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } =
            downstreamServer;

        public IAsyncDisposable? Subscription { get; set; }

        public bool HasPinnedBinding { get; set; }

        public TaskCompletionSource? ActiveAttempt { get; set; }

        public bool IsInactive =>
            Subscription is null && !HasPinnedBinding && ActiveAttempt is null;

        public bool IsRetired
        {
            get
            {
                lock (_sync)
                {
                    return _retired;
                }
            }
        }

        public bool TryRetain()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                _references++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _references--;
                return _retired && _references == 0;
            }
        }

        public void MarkRetired()
        {
            lock (_sync)
            {
                _retired = true;
            }
        }

        public void DisposeGate() => Gate.Dispose();
    }
}
