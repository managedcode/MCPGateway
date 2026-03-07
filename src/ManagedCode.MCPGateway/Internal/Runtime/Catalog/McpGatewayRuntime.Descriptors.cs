using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static McpGatewayToolDescriptor? BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        AITool tool)
    {
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

        return new McpGatewayToolDescriptor(
            ToolId: $"{registration.SourceId}:{toolName}",
            SourceId: registration.SourceId,
            SourceKind: sourceKind,
            ToolName: toolName,
            DisplayName: ResolveDisplayName(tool),
            Description: tool.Description ?? string.Empty,
            RequiredArguments: inputSchema.RequiredArguments,
            InputSchemaJson: inputSchema.Json);
    }

    private string BuildDescriptorDocument(McpGatewayToolDescriptor descriptor, AITool tool)
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

        if (descriptor.RequiredArguments.Count > 0)
        {
            builder.Append(RequiredArgumentsLabel);
            builder.AppendLine(string.Join(", ", descriptor.RequiredArguments));
        }

        AppendInputSchema(builder, descriptor.InputSchemaJson);
        var document = builder.ToString().Trim();
        return document.Length <= _maxDescriptorLength
            ? document
            : document[.._maxDescriptorLength];
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
                    var values = enumValues
                        .EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString())
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value!)
                        .ToList();
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
        if (schema is null)
        {
            return SerializedSchema.Empty;
        }

        JsonElement serializedSchema;
        try
        {
            serializedSchema = JsonSerializer.SerializeToElement(schema);
        }
        catch (InvalidOperationException) when (schema is JsonElement element && element.ValueKind == JsonValueKind.Undefined)
        {
            return SerializedSchema.Empty;
        }

        return serializedSchema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? SerializedSchema.Empty
            : new SerializedSchema(
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

        return required
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record SerializedSchema(string? Json, IReadOnlyList<string> RequiredArguments)
    {
        public static SerializedSchema Empty { get; } = new(null, []);
    }
}
