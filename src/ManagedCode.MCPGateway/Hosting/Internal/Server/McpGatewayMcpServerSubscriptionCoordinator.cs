using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerSubscriptionCoordinator(
    McpGatewayResourceSubscriptionManager resourceSubscriptionManager,
    McpGatewayPromptListNotificationManager promptListNotificationManager
)
{
    private const string StringListenerPrefix = "listen:string:";
    private const string NumberListenerPrefix = "listen:number:";
    private const string MissingListenerIdMessage =
        "A current-protocol subscriptions/listen request must have a string or integer id.";
    private readonly ConcurrentDictionary<ListenKey, ListenState> _listeners = new();

    internal int ActiveListenerCount => _listeners.Count;

    internal McpMessageFilter CreateIncomingFilter() =>
        next => async (context, cancellationToken) =>
        {
            if (
                context.JsonRpcMessage
                is not JsonRpcRequest { Method: RequestMethods.SubscriptionsListen } request
            )
            {
                await next(context, cancellationToken);
                return;
            }

            var key = ListenKey.Create(context.Server, request.Id);
            var state = new ListenState(
                CreateListenerId(request.Id),
                request.Id,
                context.Services,
                context.Server
            );
            if (!_listeners.TryAdd(key, state))
            {
                throw new McpException(
                    $"A {RequestMethods.SubscriptionsListen} request with id '{request.Id}' is already active."
                );
            }

            Exception? requestException = null;
            try
            {
                await next(context, cancellationToken);
            }
            catch (Exception exception)
            {
                requestException = exception;
            }

            Exception? cleanupException = null;
            try
            {
                if (
                    _listeners.TryGetValue(key, out var activeState)
                    && ReferenceEquals(activeState, state)
                )
                {
                    try
                    {
                        await DeactivateAsync(state);
                    }
                    finally
                    {
                        _listeners.TryRemove(
                            new KeyValuePair<ListenKey, ListenState>(key, state)
                        );
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupException = exception;
            }

            ThrowRequestOrCleanupException(requestException, cleanupException);
        };

    internal McpMessageFilter CreateOutgoingFilter() =>
        next => async (context, cancellationToken) =>
        {
            if (
                context.JsonRpcMessage
                    is not JsonRpcNotification
                    {
                        Method: NotificationMethods.SubscriptionsAcknowledgedNotification,
                    } notification
                || !TryGetSubscriptionId(notification.Params, out var subscriptionId)
                || !_listeners.TryGetValue(
                    ListenKey.Create(context.Server, subscriptionId),
                    out var state
                )
            )
            {
                await next(context, cancellationToken);
                return;
            }

            var acknowledgement = notification.Params?.Deserialize<SubscriptionsAcknowledgedNotificationParams>(
                McpJsonUtilities.DefaultOptions
            );
            if (acknowledgement is null)
            {
                await next(context, cancellationToken);
                return;
            }

            await ActivateAsync(state, acknowledgement.Notifications, cancellationToken);
            await next(context, cancellationToken);
            await state.DeliveryGate.OpenAsync(cancellationToken);
        };

    private async Task ActivateAsync(
        ListenState state,
        SubscriptionsListenNotifications granted,
        CancellationToken cancellationToken
    )
    {
        if (Interlocked.CompareExchange(ref state.ActivationState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (granted.PromptsListChanged == true)
            {
                await promptListNotificationManager.RegisterAsync(
                    state.RequestServices,
                    state.DownstreamServer,
                    state.ListenerId,
                    state.SubscriptionId,
                    state.DeliveryGate,
                    cancellationToken
                );
                state.PromptListSubscribed = true;
            }

            if (granted.ResourceSubscriptions is { Count: > 0 } resourceSubscriptions)
            {
                foreach (
                    var resourceUri in resourceSubscriptions
                        .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                        .Distinct(StringComparer.Ordinal)
                )
                {
                    await resourceSubscriptionManager.SubscribeAsync(
                        state.RequestServices,
                        state.DownstreamServer,
                        resourceUri,
                        state.ListenerId,
                        state.SubscriptionId,
                        state.DeliveryGate,
                        cancellationToken
                    );
                    state.ResourceUris.Add(resourceUri);
                }
            }

            Volatile.Write(ref state.ActivationState, 2);
        }
        catch
        {
            await DeactivateAsync(state);
            throw;
        }
    }

    private async ValueTask DeactivateAsync(ListenState state)
    {
        if (Interlocked.Exchange(ref state.ActivationState, 3) == 3)
        {
            return;
        }

        var cleanupExceptions = new List<Exception>();
        foreach (var resourceUri in state.ResourceUris)
        {
            try
            {
                await resourceSubscriptionManager.UnsubscribeAsync(
                    state.DownstreamServer,
                    resourceUri,
                    state.ListenerId,
                    CancellationToken.None
                );
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        state.ResourceUris.Clear();
        if (state.PromptListSubscribed)
        {
            try
            {
                await promptListNotificationManager.RemoveAsync(
                    state.DownstreamServer,
                    state.ListenerId
                );
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }

            state.PromptListSubscribed = false;
        }

        state.DeliveryGate.Dispose();

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private static bool TryGetSubscriptionId(JsonNode? parameters, out RequestId subscriptionId)
    {
        var value = (parameters as JsonObject)?[
            McpGatewayMcpProtocolConstants.MetaEnvelopePropertyName
        ]?[MetaKeys.SubscriptionId];
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var stringId))
            {
                subscriptionId = new RequestId(stringId);
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var longId))
            {
                subscriptionId = new RequestId(longId);
                return true;
            }
        }

        subscriptionId = default;
        return false;
    }

    private static string CreateListenerId(RequestId id) =>
        id.Id switch
        {
            string stringId => string.Concat(StringListenerPrefix, stringId),
            long longId => string.Concat(
                NumberListenerPrefix,
                longId.ToString(CultureInfo.InvariantCulture)
            ),
            _ => throw new McpException(MissingListenerIdMessage),
        };

    private static void ThrowRequestOrCleanupException(
        Exception? requestException,
        Exception? cleanupException
    )
    {
        if (requestException is not null && cleanupException is not null)
        {
            throw new AggregateException(requestException, cleanupException);
        }

        if (requestException is not null)
        {
            ExceptionDispatchInfo.Capture(requestException).Throw();
        }

        if (cleanupException is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
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

    private sealed record ListenKey(string ServerId, RequestId SubscriptionId)
    {
        public static ListenKey Create(
            ModelContextProtocol.Server.McpServer server,
            RequestId subscriptionId
        ) => new(McpGatewayMcpServerIdentity.GetInstanceId(server), subscriptionId);
    }

    private sealed class ListenState(
        string listenerId,
        RequestId subscriptionId,
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer
    )
    {
        public string ListenerId { get; } = listenerId;

        public RequestId SubscriptionId { get; } = subscriptionId;

        public IServiceProvider? RequestServices { get; } = requestServices;

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; } = downstreamServer;

        public List<string> ResourceUris { get; } = [];

        public McpGatewaySubscriptionDeliveryGate DeliveryGate { get; } = new();

        public bool PromptListSubscribed { get; set; }

        public int ActivationState;
    }
}
