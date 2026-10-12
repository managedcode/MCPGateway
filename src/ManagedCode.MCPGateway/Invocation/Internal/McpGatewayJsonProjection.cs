using System.Globalization;
using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayJsonProjection
{
    public static void Validate(IReadOnlyList<string> paths)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths.Count, McpGatewayResponseOptions.MaximumSelectPaths);
        foreach (var path in paths)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, McpGatewayResponseOptions.MaximumPointerLength);
            _ = Parse(path);
        }
    }

    public static JsonNode? Project(JsonNode? source, IReadOnlyList<string> paths)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths.Count, McpGatewayResponseOptions.MaximumSelectPaths);
        var selection = new Selection();
        foreach (var path in paths)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, McpGatewayResponseOptions.MaximumPointerLength);
            var parts = Parse(path);
            var current = selection;
            foreach (var part in parts)
            {
                if (!current.Children.TryGetValue(part, out var child))
                {
                    child = new Selection();
                    current.Children.Add(part, child);
                }
                current = child;
            }
            current.Include = true;
        }
        return Select(source, selection);
    }

    private static string[] Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0)
        {
            return [];
        }
        if (pointer[0] != '/')
        {
            throw new ArgumentException("Projection paths must be JSON Pointers.", nameof(pointer));
        }
        var parts = pointer[1..].Split('/');
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parts.Length, McpGatewayResponseOptions.MaximumPointerDepth);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            for (var character = 0; character < part.Length; character++)
            {
                if (part[character] != '~')
                {
                    continue;
                }
                if (++character == part.Length || part[character] is not ('0' or '1'))
                {
                    throw new ArgumentException("Invalid JSON Pointer escape.", nameof(pointer));
                }
            }
            parts[index] = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        return parts;
    }

    private static JsonNode? Select(JsonNode? source, Selection selection)
    {
        if (selection.Include)
        {
            return source?.DeepClone();
        }
        if (source is JsonObject document)
        {
            var projected = new JsonObject();
            foreach (var (name, child) in selection.Children)
            {
                if (document.TryGetPropertyValue(name, out var value))
                {
                    projected[name] = Select(value, child);
                }
            }
            return projected;
        }
        if (source is JsonArray array)
        {
            var projected = new JsonArray();
            foreach (var (name, child) in selection.Children)
            {
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= array.Count || name != index.ToString(CultureInfo.InvariantCulture))
                {
                    throw new ArgumentException("Array projection requires an existing explicit canonical index.");
                }
                while (projected.Count <= index)
                {
                    projected.Add(null);
                }
                projected[index] = Select(array[index], child);
            }
            return projected;
        }
        if (selection.Children.Count > 0)
        {
            throw new ArgumentException("Projection cannot traverse a scalar value.");
        }
        return null;
    }

    private sealed class Selection
    {
        public bool Include { get; set; }
        public Dictionary<string, Selection> Children { get; } = new(StringComparer.Ordinal);
    }
}
