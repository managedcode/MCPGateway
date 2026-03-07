using System.Text;
using System.Text.Json;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static SearchInput BuildSearchInput(
        McpGatewaySearchRequest request,
        string? normalizedQuery)
        => new(
            NormalizeSearchComponent(request.Query),
            NormalizeSearchComponent(normalizedQuery),
            NormalizeSearchComponent(request.ContextSummary),
            NormalizeSearchComponent(FlattenContext(request.Context)));

    private static string? NormalizeSearchComponent(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string? FlattenContext(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is not { Count: > 0 })
        {
            return null;
        }

        if (McpGatewayJsonSerializer.TrySerializeToElement(context) is not JsonElement contextElement ||
            contextElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var property in contextElement.EnumerateObject())
        {
            AppendJsonElementTerms(builder, property.Name, property.Value);
        }

        return builder.Length == 0
            ? null
            : builder.ToString();
    }

    private static void AppendJsonElementTerms(StringBuilder builder, string key, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElementTerms(builder, string.Concat(key, " ", property.Name), property.Value);
                }
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElementTerms(builder, key, item);
                }
                return;

            case JsonValueKind.String:
                if (NormalizeSearchComponent(element.GetString()) is string text)
                {
                    AppendContextTerm(builder, key, text);
                }
                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                AppendContextTerm(builder, key, element.GetBoolean() ? bool.TrueString : bool.FalseString);
                return;

            case JsonValueKind.Number:
                AppendContextTerm(builder, key, element.ToString());
                return;

            default:
                return;
        }
    }

    private static void AppendContextTerm(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append("; ");
        }

        builder.Append(key);
        builder.Append(' ');
        builder.Append(value);
    }
}
