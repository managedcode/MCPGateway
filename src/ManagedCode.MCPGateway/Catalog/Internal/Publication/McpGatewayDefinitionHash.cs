using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayDefinitionHash
{
    public static string Compute(object? value)
    {
        var normalized = Sort(McpGatewayJsonSerializer.TrySerializeToNode(value));
        var json = JsonSerializer.SerializeToUtf8Bytes(normalized, McpGatewayJsonSerializer.Options);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    public static McpGatewayCatalogDefinition Create(McpGatewayToolDescriptor tool, string bindingFingerprint)
    {
        var protocol = McpGatewayJsonSerializer.TrySerializeToElement(tool.ProtocolTool)
            ?? throw new JsonException("Catalog protocol serialization failed.");
        var schemaHash = Compute(new { tool.InputSchema, tool.OutputSchema });
        var definition = new McpGatewayCatalogDefinition(tool.ToolId, tool.SourceId, protocol, bindingFingerprint, schemaHash, string.Empty)
        { UpstreamToolName = tool.UpstreamToolName };
        return definition with { DefinitionHash = ComputeDefinition(definition) };
    }

    public static string ComputeDefinition(McpGatewayCatalogDefinition definition) => Compute(new
    {
        definition.ToolId,
        definition.SourceId,
        definition.ProtocolTool,
        definition.BindingFingerprint,
        definition.SchemaHash,
        definition.RequiredPermissions,
        definition.Provenance,
        definition.UpstreamToolName,
    });

    private static JsonNode? Sort(JsonNode? node)
    {
        if (node is JsonObject document)
        {
            var sorted = new JsonObject();
            foreach (var property in document.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                sorted.Add(property.Key, Sort(property.Value));
            }
            return sorted;
        }
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(Sort).ToArray());
        }
        return node?.DeepClone();
    }
}
