using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static McpGatewayToolDescriptor? BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        McpGatewayLoadedTool loadedTool
    )
    {
        var tool = loadedTool.Tool;
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            return null;
        }

        var toolName = tool.Name.Trim();
        var sourceKind = McpGatewaySourceKindMapper.Map(registration.Kind);

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
            InputSchemaJson: inputSchema.Json
        )
        {
            SearchAliases = searchHints.Aliases ?? [],
            SearchKeywords = searchHints.Keywords ?? [],
            Categories = searchHints.Categories ?? [],
            Tags = searchHints.Tags ?? [],
            DataSources = searchHints.DataSources ?? [],
            UsageExamples = searchHints.UsageExamples ?? [],
            IsReadOnly = searchHints.ReadOnly,
            IsIdempotent = searchHints.Idempotent,
            IsDestructive = searchHints.Destructive,
            IsOpenWorld = searchHints.OpenWorld,
            CostTier = searchHints.CostTier,
            LatencyTier = searchHints.LatencyTier,
            IsEnabledByDefault = searchHints.EnabledByDefault ?? true,
        };
    }

    private string BuildDescriptorDocument(McpGatewayToolDescriptor descriptor) =>
        BuildDescriptorDocument(descriptor, _maxDescriptorLength);

    internal static string BuildDescriptorDocument(
        McpGatewayToolDescriptor descriptor,
        int maxDescriptorLength
    )
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

        AppendDescriptorValues(builder, CategoriesLabel, descriptor.Categories);
        AppendDescriptorValues(builder, TagsLabel, descriptor.Tags);
        AppendDescriptorValues(builder, DataSourcesLabel, descriptor.DataSources);
        AppendDescriptorBoolean(builder, ReadOnlyLabel, descriptor.IsReadOnly);
        AppendDescriptorBoolean(builder, IdempotentLabel, descriptor.IsIdempotent);
        AppendDescriptorBoolean(builder, DestructiveLabel, descriptor.IsDestructive);
        AppendDescriptorBoolean(builder, OpenWorldLabel, descriptor.IsOpenWorld);
        AppendDescriptorValue(builder, CostTierLabel, descriptor.CostTier?.ToString());
        AppendDescriptorValue(builder, LatencyTierLabel, descriptor.LatencyTier?.ToString());
        AppendDescriptorValue(
            builder,
            EnabledByDefaultLabel,
            descriptor.IsEnabledByDefault ? bool.TrueString : bool.FalseString
        );

        if (descriptor.RequiredArguments.Count > 0)
        {
            builder.Append(RequiredArgumentsLabel);
            builder.AppendLine(string.Join(", ", descriptor.RequiredArguments));
        }

        AppendInputSchema(builder, descriptor.InputSchemaJson);
        AppendUsageExamples(builder, descriptor.UsageExamples);
        var document = builder.ToString().Trim();
        var effectiveMaxLength = Math.Max(
            McpGatewayOptions.MinimumDescriptorLength,
            maxDescriptorLength
        );
        return document.Length <= effectiveMaxLength ? document : document[..effectiveMaxLength];
    }

    private static void AppendDescriptorValues(
        StringBuilder builder,
        string label,
        IReadOnlyList<string> values
    )
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(label);
        builder.AppendLine(string.Join(", ", values));
    }

    private static void AppendDescriptorValue(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(label);
        builder.AppendLine(value);
    }

    private static void AppendDescriptorBoolean(StringBuilder builder, string label, bool? value)
    {
        if (value is null)
        {
            return;
        }

        AppendDescriptorValue(builder, label, value.Value ? bool.TrueString : bool.FalseString);
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
            if (!TryGetSchemaProperties(schemaDocument.RootElement, out var properties))
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                AppendInputSchemaProperty(builder, property);
            }
        }
        catch (JsonException)
        {
            builder.Append(InputSchemaLabel);
            builder.AppendLine(inputSchemaJson);
        }
    }

    private static void AppendUsageExamples(
        StringBuilder builder,
        IReadOnlyList<McpGatewayToolExample> usageExamples
    )
    {
        if (usageExamples.Count == 0)
        {
            return;
        }

        builder.AppendLine(UsageExamplesHeading);
        foreach (var usageExample in usageExamples)
        {
            if (!string.IsNullOrWhiteSpace(usageExample.Description))
            {
                builder.Append(UsageExampleDescriptionLabel);
                builder.Append(": ");
                builder.AppendLine(usageExample.Description);
            }

            builder.Append(UsageExampleInputLabel);
            builder.Append(": ");
            builder.AppendLine(usageExample.Input);

            if (!string.IsNullOrWhiteSpace(usageExample.Output))
            {
                builder.Append(UsageExampleOutputLabel);
                builder.Append(": ");
                builder.AppendLine(usageExample.Output);
            }
        }
    }

    private static bool TryGetSchemaProperties(JsonElement rootElement, out JsonElement properties)
    {
        if (
            rootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out properties)
            && properties.ValueKind == JsonValueKind.Object
        )
        {
            return true;
        }

        properties = default;
        return false;
    }

    private static void AppendInputSchemaProperty(StringBuilder builder, JsonProperty property)
    {
        builder.Append(ParameterLabel);
        builder.Append(property.Name);
        builder.Append(": ");

        AppendSchemaDescription(builder, property.Value);
        AppendSchemaType(builder, property.Value);
        AppendSchemaEnumValues(builder, property.Value);

        builder.AppendLine();
    }

    private static void AppendSchemaDescription(StringBuilder builder, JsonElement propertyValue)
    {
        if (
            !TryReadSchemaString(
                propertyValue,
                InputSchemaDescriptionPropertyName,
                out var description
            )
        )
        {
            return;
        }

        builder.Append(description);
        builder.Append(". ");
    }

    private static void AppendSchemaType(StringBuilder builder, JsonElement propertyValue)
    {
        if (!TryReadSchemaString(propertyValue, InputSchemaTypePropertyName, out var type))
        {
            return;
        }

        builder.Append(TypeLabel);
        builder.Append(type);
        builder.Append(". ");
    }

    private static void AppendSchemaEnumValues(StringBuilder builder, JsonElement propertyValue)
    {
        var values = ReadSchemaEnumValues(propertyValue);
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(TypicalValuesLabel);
        builder.Append(string.Join(", ", values));
        builder.Append(". ");
    }

    private static bool TryReadSchemaString(
        JsonElement element,
        string propertyName,
        out string? value
    )
    {
        value = null;
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IReadOnlyList<string> ReadSchemaEnumValues(JsonElement propertyValue)
    {
        if (
            !propertyValue.TryGetProperty(InputSchemaEnumPropertyName, out var enumValues)
            || enumValues.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

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

        return values;
    }

    private static string? ResolveDisplayName(AITool tool)
    {
        if (tool is McpClientTool mcpTool)
        {
            return mcpTool.ProtocolTool?.Title;
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (
            function?.AdditionalProperties is { Count: > 0 }
            && function.AdditionalProperties.TryGetValue(
                DisplayNamePropertyName,
                out var displayName
            )
            && displayName is string value
            && !string.IsNullOrWhiteSpace(value)
        )
        {
            return value;
        }

        return null;
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
        if (
            McpGatewayJsonSerializer.TrySerializeToElement(schema)
            is not JsonElement serializedSchema
        )
        {
            return SerializedSchema.Empty;
        }

        return new SerializedSchema(
            serializedSchema.GetRawText(),
            ExtractRequiredArguments(serializedSchema)
        );
    }

    private static IReadOnlyList<string> ExtractRequiredArguments(JsonElement schemaElement)
    {
        if (
            !schemaElement.TryGetProperty(InputSchemaRequiredPropertyName, out var required)
            || required.ValueKind != JsonValueKind.Array
        )
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
