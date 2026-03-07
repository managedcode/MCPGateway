using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static InvocationResolution ResolveInvocationTarget(
        ToolCatalogSnapshot snapshot,
        McpGatewayInvokeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ToolId))
        {
            var byToolId = snapshot.Entries.FirstOrDefault(item =>
                string.Equals(item.Descriptor.ToolId, request.ToolId, StringComparison.OrdinalIgnoreCase));
            return byToolId is null
                ? InvocationResolution.Fail(string.Format(CultureInfo.InvariantCulture, ToolIdNotFoundMessageFormat, request.ToolId))
                : InvocationResolution.Success(byToolId);
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            return InvocationResolution.Fail(ToolIdOrToolNameRequiredMessage);
        }

        var candidates = snapshot.Entries
            .Where(item => string.Equals(item.Descriptor.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(request.SourceId) ||
                           string.Equals(item.Descriptor.SourceId, request.SourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count switch
        {
            0 => InvocationResolution.Fail(string.Format(CultureInfo.InvariantCulture, ToolIdNotFoundMessageFormat, request.ToolName)),
            1 => InvocationResolution.Success(candidates[0]),
            _ => InvocationResolution.Fail(
                string.Format(CultureInfo.InvariantCulture, ToolNameAmbiguousMessageFormat, request.ToolName))
        };
    }

    private static object? ExtractMcpOutput(CallToolResult result)
    {
        if (result.StructuredContent is JsonElement element)
        {
            return element.Clone();
        }

        var text = result.Content?
            .OfType<TextContentBlock>()
            .FirstOrDefault(static block => !string.IsNullOrWhiteSpace(block.Text))
            ?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static object? NormalizeFunctionOutput(object? value)
    {
        return value switch
        {
            JsonElement element => NormalizeJsonElement(element),
            JsonDocument document => NormalizeJsonElement(document.RootElement),
            _ => value
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            _ => element.Clone()
        };
    }
}
