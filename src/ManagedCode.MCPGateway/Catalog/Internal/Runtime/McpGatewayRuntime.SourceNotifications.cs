using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private const string RefreshNotificationFailureMessage = "Source catalog refresh failed; the previous validated catalog remains active.";
    private readonly System.Threading.Lock _notificationGate = new();
    private readonly ConcurrentDictionary<McpGatewayToolSourceRegistration, SourceCatalogListener> _sourceNotifications = new();
    private readonly ConcurrentDictionary<SourceCatalogListener, byte> _ownedNotifications = new();
    private bool _notificationsDisposed;

    private void SynchronizeSourceNotifications(ToolCatalogSnapshot snapshot)
    {
        lock (_notificationGate)
        {
            if (_notificationsDisposed || !ReferenceEquals(Volatile.Read(ref _state).Snapshot, snapshot))
            { return; }
            foreach (var (source, tools) in snapshot.LoadedTools)
            {
                var client = (source as McpGatewayClientToolSourceRegistration)?.CachedClient
                    ?? tools.FirstOrDefault(tool => tool.Client is not null)?.Client;
                if (client?.ServerCapabilities.Tools?.ListChanged is not true || _sourceNotifications.ContainsKey(source))
                { continue; }
                var handler = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
                {
                    if (Volatile.Read(ref _state).Snapshot.LoadedTools.ContainsKey(source))
                    { _ = RefreshNotifiedSourceAsync(source.SourceId); }
                    return ValueTask.CompletedTask;
                });
                var registration = new SourceCatalogListener(client, handler, source.Kind == McpGatewaySourceRegistrationKind.Http,
                    _sourceNotificationSubscriptionTimeout, _logger);
                _ownedNotifications.TryAdd(registration, 0);
                if (!_sourceNotifications.TryAdd(source, registration))
                { _ = DisposeSourceNotificationAsync(registration); }
            }
            foreach (var source in _sourceNotifications.Keys)
            {
                if (!snapshot.LoadedTools.ContainsKey(source) && _sourceNotifications.TryRemove(source, out var registration))
                { _ = DisposeSourceNotificationAsync(registration); }
            }
        }

    }

    private async Task RefreshNotifiedSourceAsync(string sourceId)
    {
        try
        {
            if (!Volatile.Read(ref _state).IsDisposed)
            {
                await _serviceProvider.GetRequiredService<Abstractions.IMcpGatewayCatalogRuntime>().RefreshSourceAsync(sourceId);
            }
        }
        catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
        {
            _logger.LogWarning(RefreshNotificationFailureMessage);
        }
    }

    private async Task DisposeSourceNotificationAsync(SourceCatalogListener registration)
    {
        try
        { await registration.DisposeAsync(); }
        catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
        { _logger.LogWarning(RefreshNotificationFailureMessage); }
        finally { _ownedNotifications.TryRemove(registration, out _); }
    }

    private async Task DisposeSourceNotificationsAsync()
    {
        lock (_notificationGate)
        { _notificationsDisposed = true; _sourceNotifications.Clear(); }
        foreach (var registration in _ownedNotifications.Keys)
        { await DisposeSourceNotificationAsync(registration); }
    }

    private sealed class SourceCatalogListener : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly IAsyncDisposable _handler;
        private readonly Task<IAsyncDisposable?> _subscription;
        private readonly Lazy<Task> _dispose;
        public SourceCatalogListener(McpClient client, IAsyncDisposable handler, bool isHttp, TimeSpan timeout, ILogger logger)
        {
            _handler = handler;
            _subscription = isHttp ? SubscribeAsync(client, timeout, logger) : Task.FromResult<IAsyncDisposable?>(null);
            _dispose = new Lazy<Task>(DisposeOwnedAsync);
        }
        private async Task<IAsyncDisposable?> SubscribeAsync(McpClient client, TimeSpan timeout, ILogger logger)
        {
            using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            acknowledgement.CancelAfter(timeout);
            try
            { return await McpGatewayMcpClientSubscription.ListenForToolListChangesAsync(client, acknowledgement.Token); }
            catch (OperationCanceledException) when (acknowledgement.IsCancellationRequested) { return null; }
            catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
            { logger.LogWarning(RefreshNotificationFailureMessage); return null; }
        }
        public ValueTask DisposeAsync() => new(_dispose.Value);
        private async Task DisposeOwnedAsync()
        {
            await _stop.CancelAsync();
            try
            {
                var subscription = await _subscription;
                if (subscription is not null)
                { await subscription.DisposeAsync(); }
            }
            finally { try { await _handler.DisposeAsync(); } finally { _stop.Dispose(); } }
        }
    }
}
