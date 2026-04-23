using System.Collections.Concurrent;
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
            createdState.PromptChangeSubscription.Dispose();
            await bindingManager.ReleaseAsync(downstreamServer);
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

        foreach (var (_, state) in states)
        {
            await DisposeSessionAsync(state);
        }

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

            var createdSubscription = new UpstreamSubscription(source, subscription);
            if (!sessionState.UpstreamSubscriptions.TryAdd(source.SourceId, createdSubscription))
            {
                await subscription.DisposeAsync();
            }
        }
    }

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
                await DisposeSessionAsync(removedState);
            }
        }
    }

    private async Task DisposeSessionAsync(SessionState sessionState)
    {
        sessionState.PromptChangeSubscription.Dispose();

        foreach (var (_, subscription) in sessionState.UpstreamSubscriptions)
        {
            await subscription.Subscription.DisposeAsync();
        }

        sessionState.UpstreamSubscriptions.Clear();
        await bindingManager.ReleaseAsync(sessionState.DownstreamServer);
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
