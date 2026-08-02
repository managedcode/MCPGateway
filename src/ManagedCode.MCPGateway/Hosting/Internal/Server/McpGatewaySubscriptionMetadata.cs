using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewaySubscriptionMetadata
{
    private const string InvalidSubscriptionIdMessage =
        "A current-protocol subscription id must be a string or integer.";

    public static JsonObject Create(RequestId subscriptionId, JsonObject? existing = null)
    {
        var meta = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        meta[MetaKeys.SubscriptionId] = subscriptionId.Id switch
        {
            string stringId => JsonValue.Create(stringId),
            long longId => JsonValue.Create(longId),
            _ => throw new InvalidOperationException(InvalidSubscriptionIdMessage),
        };
        return meta;
    }
}
