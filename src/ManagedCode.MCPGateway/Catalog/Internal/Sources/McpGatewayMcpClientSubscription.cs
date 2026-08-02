using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayMcpClientSubscription
{
    private const string SubscriptionIdPrefix = "mcpgateway-";

    public static Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        McpClient client,
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        CancellationToken cancellationToken
    ) =>
        ListenSubscription.CreateAsync(
            client,
            new SubscriptionsListenNotifications { PromptsListChanged = true },
            NotificationMethods.PromptListChangedNotification,
            (notification, token) =>
            {
                var payload = notification.Params?.Deserialize<PromptListChangedNotificationParams>(
                    McpJsonUtilities.DefaultOptions
                ) ?? new PromptListChangedNotificationParams();
                return onChanged(payload, token);
            },
            static granted => granted.PromptsListChanged == true,
            cancellationToken
        );

    public static Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        McpClient client,
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        CancellationToken cancellationToken
    ) =>
        ListenSubscription.CreateAsync(
            client,
            new SubscriptionsListenNotifications { ResourceSubscriptions = [resourceUri] },
            NotificationMethods.ResourceUpdatedNotification,
            (notification, token) =>
            {
                var payload = notification.Params?.Deserialize<ResourceUpdatedNotificationParams>(
                    McpJsonUtilities.DefaultOptions
                );
                return payload is not null && string.Equals(payload.Uri, resourceUri, StringComparison.Ordinal)
                    ? onUpdated(payload, token)
                    : ValueTask.CompletedTask;
            },
            granted =>
                granted.ResourceSubscriptions?.Contains(resourceUri, StringComparer.Ordinal) == true,
            cancellationToken
        );

    private sealed class ListenSubscription(
        McpClient client,
        string subscriptionId,
        CancellationTokenSource lifetime,
        Task<JsonRpcResponse> listenTask,
        TaskCompletionSource<SubscriptionsListenNotifications> acknowledgement,
        IAsyncDisposable acknowledgementRegistration,
        IAsyncDisposable notificationRegistration
    ) : IAsyncDisposable
    {
        private int _disposed;

        public static async Task<IAsyncDisposable?> CreateAsync(
            McpClient client,
            SubscriptionsListenNotifications requested,
            string notificationMethod,
            Func<JsonRpcNotification, CancellationToken, ValueTask> notificationHandler,
            Func<SubscriptionsListenNotifications, bool> isGranted,
            CancellationToken cancellationToken
        )
        {
            var subscriptionId = $"{SubscriptionIdPrefix}{Guid.NewGuid():N}";
            var acknowledgement = new TaskCompletionSource<SubscriptionsListenNotifications>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var acknowledgementRegistration = client.RegisterNotificationHandler(
                NotificationMethods.SubscriptionsAcknowledgedNotification,
                (notification, _) =>
                {
                    if (!MatchesSubscription(notification.Params, subscriptionId))
                    {
                        return ValueTask.CompletedTask;
                    }

                    try
                    {
                        var payload = notification.Params?.Deserialize<SubscriptionsAcknowledgedNotificationParams>(
                            McpJsonUtilities.DefaultOptions
                        );
                        if (payload is not null)
                        {
                            acknowledgement.TrySetResult(payload.Notifications);
                        }
                    }
                    catch (JsonException exception)
                    {
                        acknowledgement.TrySetException(exception);
                    }

                    return ValueTask.CompletedTask;
                }
            );
            var notificationRegistration = client.RegisterNotificationHandler(
                notificationMethod,
                (notification, token) =>
                    MatchesSubscriptionOrIsUnscoped(notification.Params, subscriptionId)
                        ? notificationHandler(notification, token)
                        : ValueTask.CompletedTask
            );
            var lifetime = new CancellationTokenSource();
            var request = new JsonRpcRequest
            {
                Id = new RequestId(subscriptionId),
                Method = RequestMethods.SubscriptionsListen,
                Params = JsonSerializer.SerializeToNode(
                    new SubscriptionsListenRequestParams { Notifications = requested },
                    McpJsonUtilities.DefaultOptions
                ),
            };
            var subscription = new ListenSubscription(
                client,
                subscriptionId,
                lifetime,
                client.SendRequestAsync(request, lifetime.Token),
                acknowledgement,
                acknowledgementRegistration,
                notificationRegistration
            );

            try
            {
                var granted = await subscription.WaitForAcknowledgementAsync(cancellationToken);
                if (isGranted(granted))
                {
                    return subscription;
                }

                await subscription.DisposeAsync();
                return null;
            }
            catch (Exception exception)
            {
                await subscription.DisposeAfterFailureAsync(exception);
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var cleanupExceptions = new List<Exception>();
            try
            {
                await client.SendMessageAsync(
                    new JsonRpcNotification
                    {
                        Method = NotificationMethods.CancelledNotification,
                        Params = JsonSerializer.SerializeToNode(
                            new CancelledNotificationParams
                            {
                                RequestId = new RequestId(subscriptionId),
                            },
                            McpJsonUtilities.DefaultOptions
                        ),
                    },
                    CancellationToken.None
                );
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }

            await lifetime.CancelAsync();
            try
            {
                _ = await listenTask;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Cancellation is the protocol operation that closes subscriptions/listen.
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }

            await DisposeRegistrationAsync(notificationRegistration, cleanupExceptions);
            await DisposeRegistrationAsync(acknowledgementRegistration, cleanupExceptions);
            lifetime.Dispose();
            ThrowIfCleanupFailed(cleanupExceptions);
        }

        private async Task<SubscriptionsListenNotifications> WaitForAcknowledgementAsync(
            CancellationToken cancellationToken
        )
        {
            var completed = await Task.WhenAny(acknowledgement.Task, listenTask).WaitAsync(
                cancellationToken
            );
            if (ReferenceEquals(completed, acknowledgement.Task))
            {
                return await acknowledgement.Task;
            }

            _ = await listenTask;
            throw new McpException(
                $"The {RequestMethods.SubscriptionsListen} request '{subscriptionId}' completed without an acknowledgement."
            );
        }

        private async Task DisposeAfterFailureAsync(Exception requestException)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(requestException, cleanupException);
            }

            ExceptionDispatchInfo.Capture(requestException).Throw();
        }

        private static bool MatchesSubscription(JsonNode? parameters, string subscriptionId) =>
            TryGetSubscriptionId(parameters, out var receivedId)
            && string.Equals(receivedId, subscriptionId, StringComparison.Ordinal);

        private static bool MatchesSubscriptionOrIsUnscoped(
            JsonNode? parameters,
            string subscriptionId
        ) =>
            !TryGetSubscriptionId(parameters, out var receivedId)
            || string.Equals(receivedId, subscriptionId, StringComparison.Ordinal);

        private static bool TryGetSubscriptionId(JsonNode? parameters, out string? subscriptionId)
        {
            var value = (parameters as JsonObject)?[
                McpGatewayMcpProtocolConstants.MetaEnvelopePropertyName
            ]?[MetaKeys.SubscriptionId];
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringId))
            {
                subscriptionId = stringId;
                return true;
            }

            subscriptionId = null;
            return false;
        }

        private static async ValueTask DisposeRegistrationAsync(
            IAsyncDisposable registration,
            List<Exception> cleanupExceptions
        )
        {
            try
            {
                await registration.DisposeAsync();
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
    }
}
