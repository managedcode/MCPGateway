using System.Text.Json;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolSchema
{
    private const string TypePropertyName = "type";
    private const string ObjectTypeName = "object";

    public static bool IsToolObjectSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty(TypePropertyName, out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), ObjectTypeName, StringComparison.Ordinal);
}
