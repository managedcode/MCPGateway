using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static string BuildEffectiveSearchQuery(McpGatewaySearchRequest request)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            parts.Add(request.Query.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ContextSummary))
        {
            parts.Add(string.Concat(ContextSummaryPrefix, request.ContextSummary.Trim()));
        }

        var flattenedContext = FlattenContext(request.Context);
        if (!string.IsNullOrWhiteSpace(flattenedContext))
        {
            parts.Add(string.Concat(ContextPrefix, flattenedContext));
        }

        return string.Join(" | ", parts);
    }

    private static string? FlattenContext(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is not { Count: > 0 })
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var (key, value) in context)
        {
            AppendContextTerms(terms, key, value);
        }

        return terms.Count == 0
            ? null
            : string.Join("; ", terms);
    }

    private static void AppendContextTerms(List<string> terms, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        switch (value)
        {
            case string text when !string.IsNullOrWhiteSpace(text):
                terms.Add(FormattableString.Invariant($"{key} {text.Trim()}"));
                return;

            case JsonElement element:
                AppendJsonElementTerms(terms, key, element);
                return;

            case JsonNode node:
                if (node is not null)
                {
                    AppendJsonElementTerms(terms, key, JsonSerializer.SerializeToElement(node));
                }
                return;

            case IReadOnlyDictionary<string, object?> dictionary:
                foreach (var (childKey, childValue) in dictionary)
                {
                    AppendContextTerms(terms, $"{key} {childKey}", childValue);
                }
                return;

            case IEnumerable<KeyValuePair<string, object?>> dictionaryEntries:
                foreach (var (childKey, childValue) in dictionaryEntries)
                {
                    AppendContextTerms(terms, $"{key} {childKey}", childValue);
                }
                return;

            case System.Collections.IDictionary legacyDictionary:
                foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
                {
                    var childKey = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(childKey))
                    {
                        AppendContextTerms(terms, $"{key} {childKey}", entry.Value);
                    }
                }
                return;

            case System.Collections.IEnumerable enumerable when value is not string:
                foreach (var item in enumerable)
                {
                    AppendContextTerms(terms, key, item);
                }
                return;

            default:
                var scalar = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    terms.Add(FormattableString.Invariant($"{key} {scalar}"));
                }
                return;
        }
    }

    private static void AppendJsonElementTerms(List<string> terms, string key, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElementTerms(terms, $"{key} {property.Name}", property.Value);
                }
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElementTerms(terms, key, item);
                }
                return;

            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    terms.Add(FormattableString.Invariant($"{key} {text.Trim()}"));
                }
                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                terms.Add(FormattableString.Invariant($"{key} {element.GetBoolean()}"));
                return;

            case JsonValueKind.Number:
                terms.Add(FormattableString.Invariant($"{key} {element}"));
                return;

            default:
                return;
        }
    }
}
