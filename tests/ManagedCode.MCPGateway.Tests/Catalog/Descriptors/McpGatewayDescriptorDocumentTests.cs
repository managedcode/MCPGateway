using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayDescriptorDocumentTests
{
    [TUnit.Core.Test]
    public async Task BuildDescriptorDocument_AppendsSchemaDescriptionsTypesAndEnumValues()
    {
        var descriptor = new McpGatewayToolDescriptor(
            ToolId: "local:release_workflow_lookup",
            SourceId: "local",
            SourceKind: McpGatewaySourceKind.Local,
            ProtocolTool: new Tool
            {
                Name = "release_workflow_lookup",
                Title = "Release workflow lookup",
                Description = "Lookup release workflow status and approvals.",
                InputSchema = CreateInputSchema(),
            },
            RequiredArguments: ["status"]
        );

        var document = McpGatewayRuntime.BuildDescriptorDocument(
            descriptor,
            McpGatewayOptions.DefaultMaxDescriptorLength
        );

        await Assert
            .That(document)
            .Contains(
                "Parameter status: Filter by workflow status. Type string. Typical values: queued, running."
            );
        await Assert.That(document).Contains("Parameter page: Type integer.");
    }

    [TUnit.Core.Test]
    public async Task BuildDescriptorDocument_AppendsRawSchemaWhenPropertiesAreAbsent()
    {
        var descriptor = new McpGatewayToolDescriptor(
            ToolId: "local:broken_schema_lookup",
            SourceId: "local",
            SourceKind: McpGatewaySourceKind.Local,
            ProtocolTool: new Tool
            {
                Name = "broken_schema_lookup",
                Description = "Lookup with malformed schema.",
                InputSchema = JsonSerializer.SerializeToElement(
                    new { type = "object" },
                    McpGatewayJsonSerializer.Options
                ),
            },
            RequiredArguments: []
        );

        var document = McpGatewayRuntime.BuildDescriptorDocument(
            descriptor,
            McpGatewayOptions.DefaultMaxDescriptorLength
        );

        await Assert.That(document).Contains("""Input schema: {"type":"object"}""");
    }

    private static JsonElement CreateInputSchema() =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["status"] = new Dictionary<string, object?>
                    {
                        ["description"] = "Filter by workflow status",
                        ["type"] = "string",
                        ["enum"] = new object?[] { "queued", "", 7, "running", null },
                    },
                    ["page"] = new Dictionary<string, object?> { ["type"] = "integer" },
                },
            },
            McpGatewayJsonSerializer.Options
        );
}
