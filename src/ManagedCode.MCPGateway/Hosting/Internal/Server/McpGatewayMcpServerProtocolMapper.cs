#pragma warning disable MCPEXP001

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayMcpServerProtocolMapper
{
    public static Tool ToProtocolTool(
        McpGatewayToolDescriptor descriptor,
        ToolTaskSupport? taskSupport = null
    ) =>
        new()
        {
            Name = descriptor.ToolId,
            Title = descriptor.DisplayName,
            Description = descriptor.Description,
            InputSchema = ParseSchemaOrDefault(descriptor.InputSchemaJson),
            Annotations = new ToolAnnotations
            {
                Title = descriptor.DisplayName,
                ReadOnlyHint = descriptor.IsReadOnly,
                IdempotentHint = descriptor.IsIdempotent,
                DestructiveHint = descriptor.IsDestructive,
                OpenWorldHint = descriptor.IsOpenWorld,
            },
            Execution = taskSupport is null
                ? null
                : new ToolExecution { TaskSupport = taskSupport.Value },
            Meta = CreateToolMeta(descriptor),
        };

    public static Prompt ToProtocolPrompt(McpGatewayPromptDescriptor descriptor) =>
        new()
        {
            Name = descriptor.PromptId,
            Title = descriptor.DisplayName,
            Description = descriptor.Description,
            Arguments = descriptor
                .Arguments.Select(static argument => new PromptArgument
                {
                    Name = argument.Name,
                    Title = argument.DisplayName,
                    Description = argument.Description,
                    Required = argument.IsRequired,
                })
                .ToList(),
            Meta = new JsonObject
            {
                [McpGatewayMcpProtocolConstants.PromptIdMetaPropertyName] = descriptor.PromptId,
                [McpGatewayMcpProtocolConstants.PromptNameMetaPropertyName] =
                    descriptor.PromptName,
                [McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId,
            },
        };

    public static Resource ToProtocolResource(McpGatewayResourceDescriptor descriptor)
    {
        var gatewayUri = McpGatewayResourceUriCodec.ToGatewayUri(
            descriptor.SourceId,
            descriptor.ResourceUri
        );

        return new Resource
        {
            Name = CreateExportedResourceName(
                descriptor.SourceId,
                descriptor.ResourceName,
                descriptor.ResourceUri
            ),
            Title = descriptor.DisplayName,
            Uri = gatewayUri,
            Description = descriptor.Description,
            MimeType = descriptor.MimeType,
            Size = descriptor.Size,
            Meta = new JsonObject
            {
                [McpGatewayMcpProtocolConstants.ResourceNameMetaPropertyName] =
                    descriptor.ResourceName,
                [McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId,
                [McpGatewayMcpProtocolConstants.OriginalUriMetaPropertyName] =
                    descriptor.ResourceUri,
            },
        };
    }

    public static ResourceTemplate ToProtocolResourceTemplate(
        McpGatewayResourceTemplateDescriptor descriptor
    )
    {
        var gatewayUriTemplate = McpGatewayResourceUriCodec.ToGatewayUri(
            descriptor.SourceId,
            descriptor.UriTemplate
        );

        return new ResourceTemplate
        {
            Name = CreateExportedResourceName(
                descriptor.SourceId,
                descriptor.ResourceName,
                descriptor.UriTemplate
            ),
            Title = descriptor.DisplayName,
            UriTemplate = gatewayUriTemplate,
            Description = descriptor.Description,
            MimeType = descriptor.MimeType,
            Meta = new JsonObject
            {
                [McpGatewayMcpProtocolConstants.ResourceNameMetaPropertyName] =
                    descriptor.ResourceName,
                [McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId,
                [McpGatewayMcpProtocolConstants.OriginalUriTemplateMetaPropertyName] =
                    descriptor.UriTemplate,
            },
        };
    }

    public static IReadOnlyDictionary<string, object?>? ConvertArguments(
        IDictionary<string, JsonElement>? arguments
    ) =>
        arguments?.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value.Clone(),
            StringComparer.Ordinal
        );

    public static Argument CloneArgument(Argument argument) =>
        new() { Name = argument.Name, Value = argument.Value };

    public static CompleteContext? CloneContext(CompleteContext? context)
    {
        if (context?.Arguments is null || context.Arguments.Count == 0)
        {
            return null;
        }

        return new CompleteContext
        {
            Arguments = context.Arguments.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal
            ),
        };
    }

    public static CompleteResult CreateEmptyCompletionResult() =>
        new() { Completion = new Completion { Values = [] } };

    public static CallToolResult ToProtocolToolResult(McpGatewayInvokeResult invokeResult)
    {
        if (!invokeResult.IsSuccess)
        {
            return CreateErrorToolResult(invokeResult.Error ?? "Tool invocation failed.");
        }

        if (invokeResult.Output is string textOutput)
        {
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = textOutput }],
            };
        }

        var contentNode = McpGatewayJsonSerializer.TrySerializeToNode(invokeResult.Output);
        var contentElement = McpGatewayJsonSerializer.TrySerializeToElement(invokeResult.Output);
        if (contentElement is { } structuredContent)
        {
            return new CallToolResult
            {
                IsError = false,
                StructuredContent = structuredContent,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = contentNode?.ToJsonString() ?? structuredContent.GetRawText(),
                    },
                ],
            };
        }

        return new CallToolResult { IsError = false, Content = [] };
    }

    public static CallToolResult CreateErrorToolResult(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    public static GetPromptResult CreateErrorPromptResult(string message) =>
        new()
        {
            Description = message,
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = message },
                },
            ],
        };

    public static PromptMessage? ToProtocolPromptMessage(McpGatewayPromptMessage message)
    {
        if (message.Content is not null)
        {
            try
            {
                var content = JsonSerializer.Deserialize<ContentBlock>(
                    message.Content.ToJsonString(),
                    McpGatewayJsonSerializer.Options
                );
                if (content is not null)
                {
                    return new PromptMessage
                    {
                        Role = Enum.TryParse<Role>(
                            message.Role,
                            ignoreCase: true,
                            out var parsedRole
                        )
                            ? parsedRole
                            : Role.User,
                        Content = content,
                    };
                }
            }
            catch (JsonException)
            {
                // Fall back to text-only export below.
            }
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return null;
        }

        return new PromptMessage
        {
            Role = Enum.TryParse<Role>(message.Role, ignoreCase: true, out var fallbackRole)
                ? fallbackRole
                : Role.User,
            Content = new TextContentBlock { Text = message.Text },
        };
    }

    public static ResourceContents ToProtocolResourceContent(
        ResourceContents content,
        McpGatewayResolvedResourceRequest request
    )
    {
        var upstreamUri = string.IsNullOrWhiteSpace(content.Uri) ? request.UpstreamUri : content.Uri;
        var targetUri = request.UseGatewayUri
            ? McpGatewayResourceUriCodec.ToGatewayUri(request.SourceId, upstreamUri)
            : upstreamUri;
        var meta = CreateResourceContentMeta(content.Meta, request.SourceId, upstreamUri);

        if (content is TextResourceContents textContent)
        {
            return new TextResourceContents
            {
                Uri = targetUri,
                MimeType = textContent.MimeType,
                Text = textContent.Text,
                Meta = meta,
            };
        }

        if (content is BlobResourceContents blobContent)
        {
            var result = BlobResourceContents.FromBytes(
                blobContent.DecodedData,
                targetUri,
                blobContent.MimeType ?? "application/octet-stream"
            );
            result.Meta = meta;
            return result;
        }

        throw new McpException($"Unsupported resource content type '{content.GetType().Name}'.");
    }

    private static JsonElement ParseSchemaOrDefault(string? schemaJson)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                using var schemaDocument = JsonDocument.Parse(schemaJson);
                return schemaDocument.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        return JsonSerializer.SerializeToElement(
            new { type = "object", properties = new { } },
            McpGatewayJsonSerializer.Options
        );
    }

    private static JsonObject CreateToolMeta(McpGatewayToolDescriptor descriptor)
    {
        var meta = new JsonObject
        {
            [McpGatewayMcpProtocolConstants.ToolIdMetaPropertyName] = descriptor.ToolId,
            [McpGatewayMcpProtocolConstants.ToolNameMetaPropertyName] = descriptor.ToolName,
            [McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId,
            [McpGatewayMcpProtocolConstants.EnabledByDefaultMetaPropertyName] =
                descriptor.IsEnabledByDefault,
        };

        AddStringArray(
            meta,
            McpGatewayMcpProtocolConstants.CategoriesMetaPropertyName,
            descriptor.Categories
        );
        AddStringArray(meta, McpGatewayMcpProtocolConstants.TagsMetaPropertyName, descriptor.Tags);
        AddStringArray(
            meta,
            McpGatewayMcpProtocolConstants.DataSourcesMetaPropertyName,
            descriptor.DataSources
        );

        if (descriptor.CostTier is not null)
        {
            meta[McpGatewayMcpProtocolConstants.CostTierMetaPropertyName] =
                descriptor.CostTier.Value.ToString();
        }

        if (descriptor.LatencyTier is not null)
        {
            meta[McpGatewayMcpProtocolConstants.LatencyTierMetaPropertyName] =
                descriptor.LatencyTier.Value.ToString();
        }

        if (descriptor.UsageExamples.Count > 0)
        {
            meta[McpGatewayMcpProtocolConstants.UsageExamplesMetaPropertyName] = new JsonArray(
                descriptor
                    .UsageExamples.Select(static example => new JsonObject
                    {
                        ["input"] = example.Input,
                        ["output"] = example.Output,
                        ["description"] = example.Description,
                    })
                    .ToArray()
            );
        }

        return meta;
    }

    private static void AddStringArray(
        JsonObject target,
        string propertyName,
        IReadOnlyList<string> values
    )
    {
        if (values.Count == 0)
        {
            return;
        }

        target[propertyName] = new JsonArray(
            values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()
        );
    }

    private static string CreateExportedResourceName(
        string sourceId,
        string resourceName,
        string fallbackValue
    ) =>
        string.IsNullOrWhiteSpace(resourceName)
            ? $"{sourceId}:{fallbackValue}"
            : $"{sourceId}:{resourceName}";

    private static JsonObject CreateResourceContentMeta(
        JsonObject? upstreamMeta,
        string sourceId,
        string upstreamUri
    )
    {
        var meta = upstreamMeta is null ? new JsonObject() : (JsonObject)upstreamMeta.DeepClone();
        meta[McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = sourceId;
        meta[McpGatewayMcpProtocolConstants.OriginalUriMetaPropertyName] = upstreamUri;
        return meta;
    }
}

#pragma warning restore MCPEXP001
