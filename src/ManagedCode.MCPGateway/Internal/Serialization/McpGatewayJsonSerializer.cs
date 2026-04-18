using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement? TrySerializeToElement(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                JsonElement element => NormalizeElement(element),
                JsonDocument document => NormalizeElement(document.RootElement),
                JsonNode node => node is null
                    ? null
                    : NormalizeElement(JsonSerializer.SerializeToElement(node, Options)),
                _ => NormalizeElement(JsonSerializer.SerializeToElement(value, Options)),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static JsonNode? TrySerializeToNode(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                JsonElement element => SerializeElementToNode(element),
                JsonDocument document => SerializeElementToNode(document.RootElement),
                _ => JsonSerializer.SerializeToNode(value, Options),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonElement? NormalizeElement(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.Clone();

    private static JsonNode? SerializeElementToNode(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : JsonSerializer.SerializeToNode(element, Options);
}
