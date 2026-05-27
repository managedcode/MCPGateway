using System.Text.Json;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolSchema
{
    private const string JsonSchemaTypePropertyName = "type";
    private const string JsonSchemaObjectTypeName = "object";

    public static bool IsToolObjectSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty(JsonSchemaTypePropertyName, out var type)
        && type.ValueKind == JsonValueKind.String
        && type.ValueEquals(JsonSchemaObjectTypeName);
}
