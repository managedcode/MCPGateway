#pragma warning disable MCPEXP001

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProtocolUtilityTests
{
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
            "lookup",
            "Lookup",
            "Looks up a value.",
            ["value"],
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}"""
        )
        {
            Categories = ["operations"],
            Tags = ["incident"],
            DataSources = ["ops-api"],
            UsageExamples =
            [
                new McpGatewayToolExample("incident 42", "{\"status\":\"open\"}", "Checks status."),
            ],
            IsReadOnly = true,
            IsIdempotent = true,
            IsOpenWorld = true,
            CostTier = McpGatewayToolCostTier.Low,
            LatencyTier = McpGatewayToolLatencyTier.Low,
            IsEnabledByDefault = false,
        };
        var promptDescriptor = new McpGatewayPromptDescriptor(
            "local:release_review",
            "local",
            McpGatewaySourceKind.Local,
            "release_review",
            "Release review",
            "Builds a release review prompt.",
            [new McpGatewayPromptArgumentDescriptor("repository", "Repository", "Repository.", true)]
        );
        var resourceDescriptor = new McpGatewayResourceDescriptor(
            "source-a",
            McpGatewaySourceKind.CustomMcpClient,
            "overview",
            "Overview",
            "docs://overview",
            "Repository overview.",
            "text/markdown",
            42
        );
        var templateDescriptor = new McpGatewayResourceTemplateDescriptor(
            "source-a",
            McpGatewaySourceKind.CustomMcpClient,
            "issue_detail",
            "Issue detail",
            "docs://issues/{id}",
            "Issue details.",
            "application/json"
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
        await Assert.That(tool.Execution?.TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
        await Assert.That(prompt.Name).IsEqualTo("local:release_review");
        await Assert.That(prompt.Arguments?.Count).IsEqualTo(1);
        await Assert.That(resource.Name).IsEqualTo("source-a:overview");
        await Assert.That(resource.Uri).StartsWith("mcpgw-");
        await Assert.That(template.UriTemplate).StartsWith("mcpgw-");
    }

    [Test]
    public async Task ProtocolMapper_RejectsInvalidToolInputSchema()
    {
        var invalidJsonDescriptor = CreateToolDescriptor("not-json");
        var nonObjectDescriptor = CreateToolDescriptor("""["not-a-schema-object"]""");

        Exception? invalidJsonException = null;
        try
        {
            _ = McpGatewayMcpServerProtocolMapper.ToProtocolTool(invalidJsonDescriptor);
        }
        catch (Exception ex)
        {
            invalidJsonException = ex;
        }

        Exception? nonObjectException = null;
        try
        {
            _ = McpGatewayMcpServerProtocolMapper.ToProtocolTool(nonObjectDescriptor);
        }
        catch (Exception ex)
        {
            nonObjectException = ex;
        }

        await Assert.That(invalidJsonException).IsTypeOf<InvalidOperationException>();
        await Assert.That(invalidJsonException!.Message).Contains("invalid input schema JSON");
        await Assert.That(nonObjectException).IsTypeOf<InvalidOperationException>();
        await Assert.That(nonObjectException!.Message).Contains("must be a JSON object");
    }

    [Test]
    public async Task ProtocolMapper_MapsArgumentsPromptMessagesAndResourceContents()
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
        var structuredMessage = McpGatewayMcpServerProtocolMapper.ToProtocolPromptMessage(
            new McpGatewayPromptMessage(
                "assistant",
                JsonSerializer.SerializeToNode(
                    new TextContentBlock { Text = "structured" },
                    McpGatewayJsonSerializer.Options
                )
            )
        );
        var fallbackMessage = McpGatewayMcpServerProtocolMapper.ToProtocolPromptMessage(
            new McpGatewayPromptMessage(
                "user",
                new JsonObject { ["unexpected"] = "value" },
                "fallback"
            )
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
        await Assert.That(structuredMessage).IsNotNull();
        await Assert.That(((TextContentBlock)structuredMessage!.Content).Text).IsEqualTo("structured");
        await Assert.That(fallbackMessage).IsNotNull();
        await Assert.That(((TextContentBlock)fallbackMessage!.Content).Text).IsEqualTo("fallback");
        await Assert.That(textContent.Uri).StartsWith("mcpgw-");
        await Assert.That(textContent.Meta).IsTypeOf<JsonObject>();
        await Assert.That(blobContent).IsTypeOf<BlobResourceContents>();
    }

    [Test]
    public async Task ProtocolMapper_RejectsInvalidPromptMessageContentWithoutTextFallback()
    {
        Exception? exception = null;
        try
        {
            _ = McpGatewayMcpServerProtocolMapper.ToProtocolPromptMessage(
                new McpGatewayPromptMessage("user", new JsonObject { ["unexpected"] = "value" })
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("valid MCP content block");
    }

    [Test]
    public async Task ProtocolMapper_RejectsInvalidPromptMessageRole()
    {
        Exception? exception = null;
        try
        {
            _ = McpGatewayMcpServerProtocolMapper.ToProtocolPromptMessage(
                new McpGatewayPromptMessage("invalid-role", null, "hello")
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("role 'invalid-role'");
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

    private static McpGatewayToolDescriptor CreateToolDescriptor(string inputSchemaJson) =>
        new(
            "local:lookup",
            "local",
            McpGatewaySourceKind.Local,
            "lookup",
            "Lookup",
            "Looks up a value.",
            ["value"],
            inputSchemaJson
        );
}

#pragma warning restore MCPEXP001
