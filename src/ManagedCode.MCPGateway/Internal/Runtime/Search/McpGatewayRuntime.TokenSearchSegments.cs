using System.Text.Json;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static IReadOnlyList<WeightedTextSegment> BuildDescriptorTokenSearchSegments(
        McpGatewayToolDescriptor descriptor)
    {
        var segments = new List<WeightedTextSegment>();

        AddTokenSearchIdentifierSegment(segments, descriptor.ToolName, ToolNameTokenWeight);
        AddTokenSearchIdentifierSegment(segments, descriptor.DisplayName, DisplayNameTokenWeight);
        AddTokenSearchTextSegment(segments, descriptor.Description, DescriptionTokenWeight);

        foreach (var requiredArgument in descriptor.RequiredArguments)
        {
            AddTokenSearchIdentifierSegment(segments, requiredArgument, RequiredArgumentTokenWeight);
        }

        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
        {
            return segments;
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(descriptor.InputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return segments;
            }

            foreach (var property in properties.EnumerateObject())
            {
                AddTokenSearchIdentifierSegment(segments, property.Name, ParameterNameTokenWeight);

                if (property.Value.TryGetProperty(InputSchemaDescriptionPropertyName, out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    AddTokenSearchTextSegment(segments, description.GetString(), ParameterDescriptionTokenWeight);
                }

                if (property.Value.TryGetProperty(InputSchemaTypePropertyName, out var type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    AddTokenSearchIdentifierSegment(segments, type.GetString(), ParameterTypeTokenWeight);
                }

                if (property.Value.TryGetProperty(InputSchemaEnumPropertyName, out var enumValues) &&
                    enumValues.ValueKind == JsonValueKind.Array)
                {
                    foreach (var enumValue in enumValues.EnumerateArray())
                    {
                        if (enumValue.ValueKind == JsonValueKind.String)
                        {
                            AddTokenSearchIdentifierSegment(
                                segments,
                                enumValue.GetString(),
                                EnumValuesTokenWeight);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            AddTokenSearchTextSegment(segments, descriptor.InputSchemaJson, ParameterDescriptionTokenWeight);
        }

        return segments;
    }

    private static IReadOnlyList<WeightedTextSegment> BuildQueryTokenSearchSegments(
        McpGatewaySearchRequest request)
    {
        var segments = new List<WeightedTextSegment>();

        AddTokenSearchTextSegment(segments, request.Query, QueryTokenWeight);
        AddTokenSearchTextSegment(segments, request.ContextSummary, ContextSummaryTokenWeight);
        AddTokenSearchTextSegment(segments, FlattenContext(request.Context), ContextTokenWeight);

        return segments;
    }
}
