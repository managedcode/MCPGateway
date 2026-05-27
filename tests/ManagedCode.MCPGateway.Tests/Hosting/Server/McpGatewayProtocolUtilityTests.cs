#pragma warning disable MCPEXP001

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProtocolUtilityTests
{
    private static readonly string[] ValueRequiredProperties = ["value"];
    private static readonly string[] StatusRequiredProperties = ["status"];
    private static readonly string[] NonObjectSchemaValues = ["not-a-schema-object"];

    [Test]
    public async Task ResourceUriCodec_RoundTripsSchemeAndOpaqueUris()
    {
        var schemeUri = McpGatewayResourceUriCodec.ToGatewayUri("source-a", "docs://overview");
        var opaqueUri = McpGatewayResourceUriCodec.ToGatewayUri("source-b", "/tmp/file.txt");
        var opaqueTemplate = McpGatewayResourceUriCodec.ToGatewayUriTemplate(
            "source-c",
            "/files/{path}"
        );
        var decodedScheme = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            schemeUri,
            out var schemeSourceId,
            out var schemeResourceUri
        );
        var decodedOpaque = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            opaqueUri,
            out var opaqueSourceId,
            out var opaqueResourceUri
        );
        var decodedOpaqueTemplate = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            opaqueTemplate,
            out var opaqueTemplateSourceId,
            out var opaqueTemplateUri
        );
        var invalidDecode = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            "not-a-gateway-uri",
            out _,
            out _
        );

        await Assert.That(decodedScheme).IsTrue();
        await Assert.That(schemeSourceId).IsEqualTo("source-a");
        await Assert.That(schemeResourceUri).IsEqualTo("docs://overview");
        await Assert.That(decodedOpaque).IsTrue();
        await Assert.That(opaqueSourceId).IsEqualTo("source-b");
        await Assert.That(opaqueResourceUri).IsEqualTo("/tmp/file.txt");
        await Assert.That(opaqueTemplate).Contains("{path}");
        await Assert.That(decodedOpaqueTemplate).IsTrue();
        await Assert.That(opaqueTemplateSourceId).IsEqualTo("source-c");
        await Assert.That(opaqueTemplateUri).IsEqualTo("/files/{path}");
        await Assert.That(invalidDecode).IsFalse();
    }

    [Test]
    public async Task ResourceUriCodec_RejectsMalformedGatewayPayloads()
    {
        var invalidHex = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            "mcpgw-nothex+docs://overview",
            out _,
            out _
        );
        var invalidSeparator = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            "mcpgw-414243docs://overview",
            out _,
            out _
        );
        var invalidScheme = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            "1invalid:payload",
            out _,
            out _
        );
        var missingRemainder = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            "mcpgw-414243+docs:",
            out _,
            out _
        );

        await Assert.That(invalidHex).IsFalse();
        await Assert.That(invalidSeparator).IsFalse();
        await Assert.That(invalidScheme).IsFalse();
        await Assert.That(missingRemainder).IsFalse();
    }

    [Test]
    public async Task UtilityHelpers_ReturnExpectedSourceKindsAndEmptyServices()
    {
        await Assert
            .That(McpGatewaySourceKindMapper.Map(McpGatewaySourceRegistrationKind.Http))
            .IsEqualTo(McpGatewaySourceKind.HttpMcp);
        await Assert
            .That(McpGatewaySourceKindMapper.Map(McpGatewaySourceRegistrationKind.Stdio))
            .IsEqualTo(McpGatewaySourceKind.StdioMcp);
        await Assert
            .That(
                McpGatewaySourceKindMapper.Map(McpGatewaySourceRegistrationKind.CustomMcpClient)
            )
            .IsEqualTo(McpGatewaySourceKind.CustomMcpClient);
        await Assert
            .That(McpGatewaySourceKindMapper.Map(McpGatewaySourceRegistrationKind.Local))
            .IsEqualTo(McpGatewaySourceKind.Local);
        await Assert.That(EmptyServiceProvider.Instance.GetService(typeof(string))).IsNull();
    }

    [Test]
    public async Task ProtocolMapper_MapsToolPromptAndResourceContracts()
    {
        var toolDescriptor = new McpGatewayToolDescriptor(
            "local:lookup",
            "local",
            McpGatewaySourceKind.Local,
            new Tool
            {
                Name = "lookup",
                Title = "Lookup",
                Description = "Looks up a value.",
                InputSchema = JsonSerializer.SerializeToElement(
                    new
                    {
                        type = "object",
                        properties = new { value = new { type = "string" } },
                        required = ValueRequiredProperties,
                    },
                    McpGatewayJsonSerializer.Options
                ),
                Annotations = new ToolAnnotations
                {
                    Title = "Lookup",
                    ReadOnlyHint = true,
                    IdempotentHint = true,
                    OpenWorldHint = true,
                },
                Meta = new JsonObject
                {
                    ["securitySchemes"] = new JsonArray(
                        new JsonObject { ["type"] = "oauth2", ["scopes"] = new JsonArray() }
                    ),
                    ["vendor"] = "upstream",
                },
                OutputSchema = JsonSerializer.SerializeToElement(
                    new
                    {
                        type = "object",
                        properties = new { status = new { type = "string" } },
                        required = StatusRequiredProperties,
                    },
                    McpGatewayJsonSerializer.Options
                ),
            },
            ["value"]
        )
        {
            Categories = ["operations"],
            Tags = ["incident"],
            DataSources = ["ops-api"],
            UsageExamples =
            [
                new McpGatewayToolExample("incident 42", "{\"status\":\"open\"}", "Checks status."),
            ],
            CostTier = McpGatewayToolCostTier.Low,
            LatencyTier = McpGatewayToolLatencyTier.Low,
            IsEnabledByDefault = false,
        };
        var promptDescriptor = new McpGatewayPromptDescriptor(
            "local:release_review",
            "local",
            McpGatewaySourceKind.Local,
            new Prompt
            {
                Name = "release_review",
                Title = "Release review",
                Description = "Builds a release review prompt.",
                Arguments =
                [
                    new PromptArgument
                    {
                        Name = "repository",
                        Title = "Repository",
                        Description = "Repository.",
                        Required = true,
                    },
                ],
            }
        );
        var resourceDescriptor = new McpGatewayResourceDescriptor(
            "source-a",
            McpGatewaySourceKind.CustomMcpClient,
            new Resource
            {
                Name = "overview",
                Title = "Overview",
                Uri = "docs://overview",
                Description = "Repository overview.",
                MimeType = "text/markdown",
                Size = 42,
            }
        );
        var templateDescriptor = new McpGatewayResourceTemplateDescriptor(
            "source-a",
            McpGatewaySourceKind.CustomMcpClient,
            new ResourceTemplate
            {
                Name = "issue_detail",
                Title = "Issue detail",
                UriTemplate = "docs://issues/{id}",
                Description = "Issue details.",
                MimeType = "application/json",
            }
        );

        var tool = McpGatewayMcpServerProtocolMapper.ToProtocolTool(
            toolDescriptor,
            ToolTaskSupport.Optional
        );
        var prompt = McpGatewayMcpServerProtocolMapper.ToProtocolPrompt(promptDescriptor);
        var resource = McpGatewayMcpServerProtocolMapper.ToProtocolResource(resourceDescriptor);
        var template = McpGatewayMcpServerProtocolMapper.ToProtocolResourceTemplate(
            templateDescriptor
        );

        await Assert.That(tool.InputSchema.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(tool.OutputSchema?.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert
            .That(
                tool.OutputSchema
                    ?.GetProperty("properties")
                    .GetProperty("status")
                    .GetProperty("type")
                    .GetString()
            )
            .IsEqualTo("string");
        await Assert.That(tool.Meta?["vendor"]!.GetValue<string>()).IsEqualTo("upstream");
        await Assert.That(tool.Meta?["securitySchemes"]).IsTypeOf<JsonArray>();
        await Assert.That(tool.Meta?["sourceId"]!.GetValue<string>()).IsEqualTo("local");
        await Assert.That(tool.Execution?.TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
        await Assert.That(prompt.Name).IsEqualTo("local:release_review");
        await Assert.That(prompt.Arguments?.Count).IsEqualTo(1);
        await Assert.That(resource.Name).IsEqualTo("source-a:overview");
        await Assert.That(resource.Uri).StartsWith("mcpgw-");
        await Assert.That(template.UriTemplate).StartsWith("mcpgw-");
    }

    [Test]
    public async Task SdkTool_RejectsNonObjectInputSchemaBeforeGatewayMapping()
    {
        var nonObjectSchema = JsonSerializer.SerializeToElement(NonObjectSchemaValues);

        Exception? nonObjectException = null;
        try
        {
            _ = new Tool { Name = "lookup", InputSchema = nonObjectSchema };
        }
        catch (Exception ex)
        {
            nonObjectException = ex;
        }

        await Assert.That(nonObjectException).IsTypeOf<ArgumentException>();
        await Assert.That(nonObjectException!.Message).Contains("not a valid MCP tool input");
    }

    [Test]
    public async Task ProtocolMapper_MapsArgumentsAndResourceContents()
    {
        using var document = JsonDocument.Parse("""{"value":"alpha"}""");
        var arguments = McpGatewayMcpServerProtocolMapper.ConvertArguments(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["payload"] = document.RootElement.Clone(),
            }
        );
        var clonedContext = McpGatewayMcpServerProtocolMapper.CloneContext(
            new CompleteContext
            {
                Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["environment"] = "prod-eu",
                },
            }
        );
        var textContent = McpGatewayMcpServerProtocolMapper.ToProtocolResourceContent(
            new TextResourceContents
            {
                Uri = "docs://overview",
                MimeType = "text/plain",
                Text = "hello",
                Meta = new JsonObject { ["region"] = "eu" },
            },
            new McpGatewayResolvedResourceRequest(
                "source-a",
                "docs://overview",
                "mcpgw://overview",
                UseGatewayUri: true,
                Source: new McpGatewayRegistrationBoundServerSource(
                    new McpGatewayLocalToolSourceRegistration("source-a", null)
                )
            )
        );
        var blobContent = McpGatewayMcpServerProtocolMapper.ToProtocolResourceContent(
            BlobResourceContents.FromBytes(
                new byte[] { 0x01, 0x02 },
                "docs://archive",
                "application/octet-stream"
            ),
            new McpGatewayResolvedResourceRequest(
                "source-a",
                "docs://archive",
                "docs://archive",
                UseGatewayUri: false,
                Source: new McpGatewayRegistrationBoundServerSource(
                    new McpGatewayLocalToolSourceRegistration("source-a", null)
                )
            )
        );

        await Assert.That(arguments).IsNotNull();
        await Assert
            .That(((JsonElement)arguments!["payload"]!).GetProperty("value").GetString())
            .IsEqualTo("alpha");
        await Assert.That(clonedContext?.Arguments?["environment"]).IsEqualTo("prod-eu");
        await Assert.That(textContent.Uri).StartsWith("mcpgw-");
        await Assert.That(textContent.Meta).IsTypeOf<JsonObject>();
        await Assert.That(blobContent).IsTypeOf<BlobResourceContents>();
    }

    [Test]
    public async Task ProtocolMapper_MapsToolAndPromptResults()
    {
        var successResult = McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(
            new McpGatewayInvokeResult(
                true,
                "local:lookup",
                "local",
                "lookup",
                new { status = "ok" }
            )
        );
        var emptyResult = McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(
            new McpGatewayInvokeResult(true, "local:none", "local", "none", null)
        );
        var errorResult = McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(
            new McpGatewayInvokeResult(
                false,
                "local:lookup",
                "local",
                "lookup",
                null,
                Error: "boom"
            )
        );
        var promptError = McpGatewayMcpServerProtocolMapper.CreateErrorPromptResult("missing");
        var emptyCompletion = McpGatewayMcpServerProtocolMapper.CreateEmptyCompletionResult();

        await Assert.That(successResult.IsError).IsFalse();
        await Assert.That(successResult.StructuredContent).IsNotNull();
        await Assert.That(emptyResult.Content.Count).IsEqualTo(0);
        await Assert.That(errorResult.IsError).IsTrue();
        await Assert.That(((TextContentBlock)errorResult.Content.Single()).Text).IsEqualTo("boom");
        await Assert.That(promptError.Description).IsEqualTo("missing");
        await Assert.That(promptError.Messages.Count).IsEqualTo(1);
        await Assert.That(emptyCompletion.Completion.Values.Count).IsEqualTo(0);
    }
}

#pragma warning restore MCPEXP001
