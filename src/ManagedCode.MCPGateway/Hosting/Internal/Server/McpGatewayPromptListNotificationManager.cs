using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptListNotificationManager(
    McpGatewayMcpServerBindingManager bindingManager,
    IServiceProvider serviceProvider,
    ILogger<McpGatewayPromptListNotificationManager> logger,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public async Task RegisterDownstreamServerAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = McpGatewayMcpServerIdentity.GetKey(downstreamServer);

        if (_sessions.TryGetValue(sessionId, out var existingState))
        {
            existingState.DownstreamServer = downstreamServer;
            await RefreshUpstreamSubscriptionsAsync(existingState, cancellationToken);
            return;
        }

        await using var bindingLease = await bindingManager.PinAsync(
            requestServices,
            serviceProvider,
            downstreamServer,
            cancellationToken
        );

        var createdState = new SessionState(
            sessionId,
            downstreamServer,
            bindingLease.Binding,
            bindingLease.Binding.SubscribeToPromptListChanges(
                () => _ = NotifyPromptListChangedAsync(sessionId, CancellationToken.None)
            )
        );

        if (!_sessions.TryAdd(sessionId, createdState))
        {
            await DisposeSessionAsync(createdState);
            return;
        }

        await RefreshUpstreamSubscriptionsAsync(createdState, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var states = _sessions.ToArray();
        _sessions.Clear();
        var cleanupExceptions = new List<Exception>();

        foreach (var (_, state) in states)
        {
            await DisposeSessionAsync(state, cleanupExceptions);
        }

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private async Task RefreshUpstreamSubscriptionsAsync(
        SessionState sessionState,
        CancellationToken cancellationToken
    )
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var activeSources = (await sessionState.Binding.ListSourcesAsync(cancellationToken)).ToDictionary(
            static source => source.SourceId,
            StringComparer.Ordinal
        );

        foreach (var (sourceId, existingSubscription) in sessionState.UpstreamSubscriptions.ToArray())
        {
            if (
                !activeSources.TryGetValue(sourceId, out var source)
                || !ReferenceEquals(existingSubscription.Source, source)
            )
            {
                if (sessionState.UpstreamSubscriptions.TryRemove(sourceId, out var removed))
                {
                    await removed.Subscription.DisposeAsync();
                }
            }
        }

        foreach (var source in activeSources.Values)
        {
            if (sessionState.UpstreamSubscriptions.ContainsKey(source.SourceId))
            {
                continue;
            }

            var subscription = await source.SubscribeToPromptListChangesAsync(
                (_, token) =>
                    new ValueTask(
                        ForwardUpstreamPromptListChangedAsync(sessionState.SessionId, source.SourceId, token)
                    ),
                loggerFactory,
                cancellationToken
            );
            if (subscription is null)
            {
                continue;
            }

            if (!IsCurrentSession(sessionState))
            {
                await subscription.DisposeAsync();
                continue;
            }

            var createdSubscription = new UpstreamSubscription(source, subscription);
            if (!sessionState.UpstreamSubscriptions.TryAdd(source.SourceId, createdSubscription))
            {
                await subscription.DisposeAsync();
                continue;
            }

            if (
                !IsCurrentSession(sessionState)
                && sessionState.UpstreamSubscriptions.TryRemove(source.SourceId, out var removed)
            )
            {
                await removed.Subscription.DisposeAsync();
            }
        }
    }

    private bool IsCurrentSession(SessionState sessionState) =>
        _sessions.TryGetValue(sessionState.SessionId, out var activeSession)
        && ReferenceEquals(activeSession, sessionState);

    private Task ForwardUpstreamPromptListChangedAsync(
        string sessionId,
        string sourceId,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug(
            "Forwarding MCP prompt list changed notification from upstream source '{SourceId}' to session '{SessionId}'.",
            sourceId,
            sessionId
        );

        return NotifyPromptListChangedAsync(sessionId, cancellationToken);
    }

    private async Task NotifyPromptListChangedAsync(
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        if (!_sessions.TryGetValue(sessionId, out var sessionState))
        {
            return;
        }

        try
        {
            await sessionState.DownstreamServer.SendNotificationAsync(
                NotificationMethods.PromptListChangedNotification,
                new PromptListChangedNotificationParams(),
                McpJsonUtilities.DefaultOptions,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to send MCP prompt list changed notification to session '{SessionId}'. Removing the downstream subscription.",
                sessionId
            );

            if (_sessions.TryRemove(sessionId, out var removedState))
            {
                var cleanupExceptions = new List<Exception>();
                await DisposeSessionAsync(removedState, cleanupExceptions);
                if (cleanupExceptions.Count > 0)
                {
                    logger.LogDebug(
                        new AggregateException(cleanupExceptions),
                        "Failed to clean up MCP prompt list notification session '{SessionId}' after notification forwarding failed.",
                        sessionId
                    );
                }
            }
        }
    }

    private async Task DisposeSessionAsync(SessionState sessionState)
    {
        var cleanupExceptions = new List<Exception>();
        await DisposeSessionAsync(sessionState, cleanupExceptions);
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private async Task DisposeSessionAsync(
        SessionState sessionState,
        List<Exception> cleanupExceptions
    )
    {
        try
        {
            sessionState.PromptChangeSubscription.Dispose();
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
        }

        foreach (var (_, subscription) in sessionState.UpstreamSubscriptions)
        {
            try
            {
                await subscription.Subscription.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        sessionState.UpstreamSubscriptions.Clear();
        try
        {
            await bindingManager.ReleaseAsync(sessionState.DownstreamServer);
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
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

    private sealed class SessionState(
        string sessionId,
        ModelContextProtocol.Server.McpServer downstreamServer,
        IMcpGatewayServerBinding binding,
        IDisposable promptChangeSubscription
    )
    {
        public string SessionId { get; } = sessionId;

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } =
            downstreamServer;

        public IMcpGatewayServerBinding Binding { get; } = binding;

        public IDisposable PromptChangeSubscription { get; } = promptChangeSubscription;

        public ConcurrentDictionary<string, UpstreamSubscription> UpstreamSubscriptions { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed record UpstreamSubscription(
        IMcpGatewayServerSource Source,
        IAsyncDisposable Subscription
    );
}
