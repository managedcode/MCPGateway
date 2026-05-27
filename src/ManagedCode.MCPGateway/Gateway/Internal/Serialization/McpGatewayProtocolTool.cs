#pragma warning disable MCPEXP001

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolTool
{
    public static Tool Clone(Tool tool) =>
        new()
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = CloneInputSchema(tool.InputSchema),
            OutputSchema = CloneOutputSchema(tool.OutputSchema),
            Annotations = CloneAnnotations(tool.Annotations),
            Execution = CloneExecution(tool.Execution),
            Icons = CloneIcons(tool.Icons),
            Meta = CloneMeta(tool.Meta),
        };

    public static JsonElement CreateDefaultObjectSchema() =>
        JsonSerializer.SerializeToElement(
            new { type = "object", properties = new { } },
            McpGatewayJsonSerializer.Options
        );

    public static ToolAnnotations? CloneAnnotations(ToolAnnotations? annotations) =>
        annotations is null
            ? null
            : new ToolAnnotations
            {
                Title = annotations.Title,
                ReadOnlyHint = annotations.ReadOnlyHint,
                IdempotentHint = annotations.IdempotentHint,
                DestructiveHint = annotations.DestructiveHint,
                OpenWorldHint = annotations.OpenWorldHint,
            };

    public static JsonObject? CloneMeta(JsonObject? meta) =>
        meta is null ? null : (JsonObject)meta.DeepClone();

    private static JsonElement CloneInputSchema(JsonElement schema) =>
        McpGatewayProtocolSchema.IsToolObjectSchema(schema)
            ? schema.Clone()
            : CreateDefaultObjectSchema();

    private static JsonElement? CloneOutputSchema(JsonElement? schema)
    {
        if (schema is not { } schemaValue || schemaValue.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return McpGatewayProtocolSchema.IsToolObjectSchema(schemaValue) ? schemaValue.Clone() : null;
    }

    private static ToolExecution? CloneExecution(ToolExecution? execution) =>
        execution is null ? null : new ToolExecution { TaskSupport = execution.TaskSupport };

    private static IList<Icon>? CloneIcons(IList<Icon>? icons) =>
        icons?.Select(static icon => new Icon
        {
            Source = icon.Source,
            MimeType = icon.MimeType,
            Sizes = icon.Sizes?.ToArray(),
            Theme = icon.Theme,
        }).ToList();
}

#pragma warning restore MCPEXP001
