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
            ToolName: "release_workflow_lookup",
            DisplayName: "Release workflow lookup",
            Description: "Lookup release workflow status and approvals.",
            RequiredArguments: ["status"],
            InputSchemaJson: """
            {
              "type": "object",
              "properties": {
                "status": {
                  "description": "Filter by workflow status",
                  "type": "string",
                  "enum": ["queued", "", 7, "running", null]
                },
                "page": {
                  "type": "integer"
                }
              }
            }
            """
        );

        var document = McpGatewayRuntime.BuildDescriptorDocument(descriptor, 4096);

        await Assert
            .That(document)
            .Contains(
                "Parameter status: Filter by workflow status. Type string. Typical values: queued, running."
            );
        await Assert.That(document).Contains("Parameter page: Type integer.");
    }

    [TUnit.Core.Test]
    public async Task BuildDescriptorDocument_FallsBackToRawSchemaWhenJsonIsMalformed()
    {
        var descriptor = new McpGatewayToolDescriptor(
            ToolId: "local:broken_schema_lookup",
            SourceId: "local",
            SourceKind: McpGatewaySourceKind.Local,
            ToolName: "broken_schema_lookup",
            DisplayName: null,
            Description: "Lookup with malformed schema.",
            RequiredArguments: [],
            InputSchemaJson: "{ not-valid-json"
        );

        var document = McpGatewayRuntime.BuildDescriptorDocument(descriptor, 4096);

        await Assert.That(document).Contains("Input schema: { not-valid-json");
    }
}
