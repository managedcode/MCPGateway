using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement? TrySerializeToElement(object? value)
        => value switch
        {
            null => null,
            JsonElement element => NormalizeElement(element),
            JsonDocument document => NormalizeElement(document.RootElement),
            JsonNode node => node is null ? null : NormalizeElement(JsonSerializer.SerializeToElement(node, Options)),
            _ => NormalizeElement(JsonSerializer.SerializeToElement(value, Options))
        };

    public static JsonNode? TrySerializeToNode(object? value)
        => value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => SerializeElementToNode(element),
            JsonDocument document => SerializeElementToNode(document.RootElement),
            _ => JsonSerializer.SerializeToNode(value, Options)
        };

    private static JsonSerializerOptions CreateOptions()
        => new(JsonSerializerDefaults.Web);

    private static JsonElement? NormalizeElement(JsonElement element)
        => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : element.Clone();

    private static JsonNode? SerializeElementToNode(JsonElement element)
        => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : JsonSerializer.SerializeToNode(element, Options);
}
