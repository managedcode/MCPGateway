using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static McpGatewayToolDescriptor? BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        McpGatewayLoadedTool loadedTool)
    {
        var tool = loadedTool.Tool;
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            return null;
        }

        var toolName = tool.Name.Trim();
        var sourceKind = registration.Kind switch
        {
            McpGatewaySourceRegistrationKind.Http => McpGatewaySourceKind.HttpMcp,
            McpGatewaySourceRegistrationKind.Stdio => McpGatewaySourceKind.StdioMcp,
            McpGatewaySourceRegistrationKind.CustomMcpClient => McpGatewaySourceKind.CustomMcpClient,
            _ => McpGatewaySourceKind.Local
        };

        var inputSchema = ResolveInputSchema(tool);
        var searchHints = ResolveSearchHints(tool, loadedTool.SearchHints);

        return new McpGatewayToolDescriptor(
            ToolId: $"{registration.SourceId}:{toolName}",
            SourceId: registration.SourceId,
            SourceKind: sourceKind,
            ToolName: toolName,
            DisplayName: ResolveDisplayName(tool),
            Description: tool.Description ?? string.Empty,
            RequiredArguments: inputSchema.RequiredArguments,
            InputSchemaJson: inputSchema.Json)
        {
            SearchAliases = searchHints.Aliases ?? [],
            SearchKeywords = searchHints.Keywords ?? []
        };
    }

    private string BuildDescriptorDocument(McpGatewayToolDescriptor descriptor)
        => BuildDescriptorDocument(descriptor, _maxDescriptorLength);

    internal static string BuildDescriptorDocument(
        McpGatewayToolDescriptor descriptor,
        int maxDescriptorLength)
    {
        var builder = new StringBuilder();
        builder.Append(ToolNameLabel);
        builder.AppendLine(descriptor.ToolName);

        if (!string.IsNullOrWhiteSpace(descriptor.DisplayName))
        {
            builder.Append(DisplayNameLabel);
            builder.AppendLine(descriptor.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            builder.Append(DescriptionLabel);
            builder.AppendLine(descriptor.Description);
        }

        if (descriptor.SearchAliases.Count > 0)
        {
            builder.Append(SearchAliasesLabel);
            builder.AppendLine(string.Join(", ", descriptor.SearchAliases));
        }

        if (descriptor.SearchKeywords.Count > 0)
        {
            builder.Append(SearchKeywordsLabel);
            builder.AppendLine(string.Join(", ", descriptor.SearchKeywords));
        }

        if (descriptor.RequiredArguments.Count > 0)
        {
            builder.Append(RequiredArgumentsLabel);
            builder.AppendLine(string.Join(", ", descriptor.RequiredArguments));
        }

        AppendInputSchema(builder, descriptor.InputSchemaJson);
        var document = builder.ToString().Trim();
        var effectiveMaxLength = Math.Max(256, maxDescriptorLength);
        return document.Length <= effectiveMaxLength
            ? document
            : document[..effectiveMaxLength];
    }

    private static void AppendInputSchema(StringBuilder builder, string? inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return;
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(inputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                builder.Append(ParameterLabel);
                builder.Append(property.Name);
                builder.Append(": ");

                if (property.Value.TryGetProperty(InputSchemaDescriptionPropertyName, out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    builder.Append(description.GetString());
                    builder.Append(". ");
                }

                if (property.Value.TryGetProperty(InputSchemaTypePropertyName, out var type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    builder.Append(TypeLabel);
                    builder.Append(type.GetString());
                    builder.Append(". ");
                }

                if (property.Value.TryGetProperty(InputSchemaEnumPropertyName, out var enumValues) &&
                    enumValues.ValueKind == JsonValueKind.Array)
                {
                    var values = new List<string>();
                    foreach (var enumValue in enumValues.EnumerateArray())
                    {
                        if (enumValue.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var value = enumValue.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values.Add(value);
                        }
                    }

                    if (values.Count > 0)
                    {
                        builder.Append(TypicalValuesLabel);
                        builder.Append(string.Join(", ", values));
                        builder.Append(". ");
                    }
                }

                builder.AppendLine();
            }
        }
        catch (JsonException)
        {
            builder.Append(InputSchemaLabel);
            builder.AppendLine(inputSchemaJson);
        }
    }

    private static string? ResolveDisplayName(AITool tool)
    {
        if (tool is McpClientTool mcpTool)
        {
            return mcpTool.ProtocolTool?.Title;
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (function?.AdditionalProperties is { Count: > 0 } &&
            function.AdditionalProperties.TryGetValue(DisplayNamePropertyName, out var displayName) &&
            displayName is string value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    private static McpGatewayToolSearchHints ResolveSearchHints(
        AITool tool,
        McpGatewayToolSearchHints? registeredHints)
    {
        var aliases = new List<string>();
        var keywords = new List<string>();
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSearchHintValues(aliases, seenAliases, registeredHints?.Aliases);
        AddSearchHintValues(keywords, seenKeywords, registeredHints?.Keywords);

        if (tool is McpClientTool mcpTool)
        {
            AddSerializedSearchHints(aliases, seenAliases, keywords, seenKeywords, mcpTool.ProtocolTool?.Annotations);
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (function?.AdditionalProperties is { Count: > 0 })
        {
            AddSerializedSearchHints(aliases, seenAliases, keywords, seenKeywords, function.AdditionalProperties);
        }

        return new McpGatewayToolSearchHints(aliases, keywords);
    }

    private static void AddSerializedSearchHints(
        ICollection<string> aliases,
        ISet<string> seenAliases,
        ICollection<string> keywords,
        ISet<string> seenKeywords,
        object? value)
    {
        if (McpGatewayJsonSerializer.TrySerializeToElement(value) is not JsonElement element ||
            element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AddSearchHintValues(aliases, seenAliases, ReadSearchHintValues(
            element,
            SearchAliasesPropertyName,
            SearchAliasesCamelCasePropertyName,
            SearchAliasesSnakeCasePropertyName,
            SearchAliasesShortPropertyName));
        AddSearchHintValues(keywords, seenKeywords, ReadSearchHintValues(
            element,
            SearchKeywordsPropertyName,
            SearchKeywordsCamelCasePropertyName,
            SearchKeywordsSnakeCasePropertyName,
            SearchKeywordsShortPropertyName));
    }

    private static IReadOnlyList<string> ReadSearchHintValues(
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            return ReadSearchHintValues(property);
        }

        return [];
    }

    private static IReadOnlyList<string> ReadSearchHintValues(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString() ?? string.Empty],
            JsonValueKind.Array => element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty)
                .ToArray(),
            _ => []
        };
    }

    private static void AddSearchHintValues(
        ICollection<string> target,
        ISet<string> seenValues,
        IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim();
            if (!seenValues.Add(normalized))
            {
                continue;
            }

            target.Add(normalized);
        }
    }

    private static SerializedSchema ResolveInputSchema(AITool tool)
    {
        if (tool is McpClientTool mcpTool)
        {
            return SerializeSchema(mcpTool.ProtocolTool?.InputSchema);
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (function is null)
        {
            return SerializedSchema.Empty;
        }

        return function.JsonSchema.ValueKind == JsonValueKind.Undefined
            ? SerializedSchema.Empty
            : SerializeSchema(function.JsonSchema);
    }

    private static SerializedSchema SerializeSchema(object? schema)
    {
        if (McpGatewayJsonSerializer.TrySerializeToElement(schema) is not JsonElement serializedSchema)
        {
            return SerializedSchema.Empty;
        }

        return new SerializedSchema(
            serializedSchema.GetRawText(),
            ExtractRequiredArguments(serializedSchema));
    }

    private static IReadOnlyList<string> ExtractRequiredArguments(JsonElement schemaElement)
    {
        if (!schemaElement.TryGetProperty(InputSchemaRequiredPropertyName, out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var requiredArguments = new List<string>();
        var seenArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value) || !seenArguments.Add(value))
            {
                continue;
            }

            requiredArguments.Add(value);
        }

        return requiredArguments;
    }

    private sealed record SerializedSchema(string? Json, IReadOnlyList<string> RequiredArguments)
    {
        public static SerializedSchema Empty { get; } = new(null, []);
    }
}
