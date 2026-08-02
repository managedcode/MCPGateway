using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class McpTestSubscriptionListener : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Task<JsonRpcResponse> _listenTask;
    private readonly IAsyncDisposable _acknowledgementRegistration;
    private readonly McpClient _client;
    private readonly string _subscriptionId;
    private int _disposed;

    private McpTestSubscriptionListener(
        McpClient client,
        string subscriptionId,
        CancellationTokenSource lifetime,
        Task<JsonRpcResponse> listenTask,
        IAsyncDisposable acknowledgementRegistration,
        SubscriptionsListenNotifications granted
    )
    {
        _client = client;
        _subscriptionId = subscriptionId;
        _lifetime = lifetime;
        _listenTask = listenTask;
        _acknowledgementRegistration = acknowledgementRegistration;
        Granted = granted;
    }

    public SubscriptionsListenNotifications Granted { get; }

    public static async Task<McpTestSubscriptionListener> ListenAsync(
        McpClient client,
        SubscriptionsListenNotifications notifications,
        CancellationToken cancellationToken = default
    )
    {
        var subscriptionId = $"test-{Guid.NewGuid():N}";
        var acknowledgement = new TaskCompletionSource<SubscriptionsListenNotifications>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var acknowledgementRegistration = client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (notification, _) =>
            {
                var receivedId = (notification.Params as JsonObject)?[
                    McpGatewayMcpProtocolConstants.MetaEnvelopePropertyName
                ]?[MetaKeys.SubscriptionId]?.GetValue<string>();
                if (!string.Equals(receivedId, subscriptionId, StringComparison.Ordinal))
                {
                    return ValueTask.CompletedTask;
                }

                var payload = notification.Params?.Deserialize<SubscriptionsAcknowledgedNotificationParams>(
                    McpJsonUtilities.DefaultOptions
                );
                if (payload is not null)
                {
                    acknowledgement.TrySetResult(payload.Notifications);
                }

                return ValueTask.CompletedTask;
            }
        );
        var lifetime = new CancellationTokenSource();
        var request = new JsonRpcRequest
        {
            Id = new RequestId(subscriptionId),
            Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams { Notifications = notifications },
                McpJsonUtilities.DefaultOptions
            ),
        };
        var listenTask = client.SendRequestAsync(request, lifetime.Token);

        try
        {
            var completed = await Task.WhenAny(acknowledgement.Task, listenTask).WaitAsync(
                cancellationToken
            );
            if (ReferenceEquals(completed, acknowledgement.Task))
            {
                return new McpTestSubscriptionListener(
                    client,
                    subscriptionId,
                    lifetime,
                    listenTask,
                    acknowledgementRegistration,
                    await acknowledgement.Task
                );
            }

            _ = await listenTask;
            throw new InvalidOperationException(
                "subscriptions/listen completed without an acknowledgement."
            );
        }
        catch
        {
            await lifetime.CancelAsync();
            await acknowledgementRegistration.DisposeAsync();
            lifetime.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? listenException = null;
        await _client.SendMessageAsync(
            new JsonRpcNotification
            {
                Method = NotificationMethods.CancelledNotification,
                Params = JsonSerializer.SerializeToNode(
                    new CancelledNotificationParams
                    {
                        RequestId = new RequestId(_subscriptionId),
                    },
                    McpJsonUtilities.DefaultOptions
                ),
            },
            CancellationToken.None
        );
        await _lifetime.CancelAsync();
        try
        {
            _ = await _listenTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Cancellation closes the protocol subscription stream.
        }
        catch (Exception exception)
        {
            listenException = exception;
        }

        Exception? registrationException = null;
        try
        {
            await _acknowledgementRegistration.DisposeAsync();
        }
        catch (Exception exception)
        {
            registrationException = exception;
        }

        _lifetime.Dispose();
        if (listenException is not null && registrationException is not null)
        {
            throw new AggregateException(listenException, registrationException);
        }

        if (listenException is not null)
        {
            ExceptionDispatchInfo.Capture(listenException).Throw();
        }

        if (registrationException is not null)
        {
            ExceptionDispatchInfo.Capture(registrationException).Throw();
        }
    }
}
