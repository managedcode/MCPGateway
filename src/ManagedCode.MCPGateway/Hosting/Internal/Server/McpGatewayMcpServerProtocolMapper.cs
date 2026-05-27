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
    )
    {
        var tool = McpGatewayProtocolTool.Clone(descriptor.ProtocolTool);
        tool.Name = descriptor.ToolId;
        tool.Meta = MergeToolMeta(tool.Meta, descriptor);
        tool.Execution = taskSupport is null
            ? tool.Execution
            : new ToolExecution { TaskSupport = taskSupport.Value };

        if (!string.IsNullOrWhiteSpace(tool.Title))
        {
            tool.Annotations ??= new ToolAnnotations();
            tool.Annotations.Title ??= tool.Title;
        }

        return tool;
    }

    public static Prompt ToProtocolPrompt(McpGatewayPromptDescriptor descriptor)
    {
        var prompt = McpGatewayProtocolPrimitive.Clone(descriptor.ProtocolPrompt);
        prompt.Name = descriptor.PromptId;
        prompt.Meta = MergePromptMeta(prompt.Meta, descriptor);
        return prompt;
    }

    public static Resource ToProtocolResource(McpGatewayResourceDescriptor descriptor)
    {
        var gatewayUri = McpGatewayResourceUriCodec.ToGatewayUri(
            descriptor.SourceId,
            descriptor.ResourceUri
        );

        var resource = McpGatewayProtocolPrimitive.Clone(descriptor.ProtocolResource);
        resource.Name = CreateExportedResourceName(
            descriptor.SourceId,
            descriptor.ResourceName,
            descriptor.ResourceUri
        );
        resource.Uri = gatewayUri;
        resource.Meta = MergeResourceMeta(
            resource.Meta,
            descriptor.SourceId,
            descriptor.ResourceName,
            descriptor.ResourceUri
        );
        return resource;
    }

    public static ResourceTemplate ToProtocolResourceTemplate(
        McpGatewayResourceTemplateDescriptor descriptor
    )
    {
        var gatewayUriTemplate = McpGatewayResourceUriCodec.ToGatewayUriTemplate(
            descriptor.SourceId,
            descriptor.UriTemplate
        );

        var resourceTemplate = McpGatewayProtocolPrimitive.Clone(
            descriptor.ProtocolResourceTemplate
        );
        resourceTemplate.Name = CreateExportedResourceName(
            descriptor.SourceId,
            descriptor.ResourceName,
            descriptor.UriTemplate
        );
        resourceTemplate.UriTemplate = gatewayUriTemplate;
        resourceTemplate.Meta = MergeResourceTemplateMeta(
            resourceTemplate.Meta,
            descriptor.SourceId,
            descriptor.ResourceName,
            descriptor.UriTemplate
        );
        return resourceTemplate;
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

    private static JsonObject MergeToolMeta(
        JsonObject? meta,
        McpGatewayToolDescriptor descriptor
    )
    {
        meta ??= new JsonObject();
        meta[McpGatewayMcpProtocolConstants.ToolIdMetaPropertyName] = descriptor.ToolId;
        meta[McpGatewayMcpProtocolConstants.ToolNameMetaPropertyName] = descriptor.ToolName;
        meta[McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId;
        meta[McpGatewayMcpProtocolConstants.EnabledByDefaultMetaPropertyName] =
            descriptor.IsEnabledByDefault;

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

    private static JsonObject MergePromptMeta(
        JsonObject? meta,
        McpGatewayPromptDescriptor descriptor
    )
    {
        meta ??= new JsonObject();
        meta[McpGatewayMcpProtocolConstants.PromptIdMetaPropertyName] = descriptor.PromptId;
        meta[McpGatewayMcpProtocolConstants.PromptNameMetaPropertyName] = descriptor.PromptName;
        meta[McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = descriptor.SourceId;
        return meta;
    }

    private static JsonObject MergeResourceMeta(
        JsonObject? meta,
        string sourceId,
        string resourceName,
        string originalUri
    )
    {
        meta ??= new JsonObject();
        meta[McpGatewayMcpProtocolConstants.ResourceNameMetaPropertyName] = resourceName;
        meta[McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = sourceId;
        meta[McpGatewayMcpProtocolConstants.OriginalUriMetaPropertyName] = originalUri;
        return meta;
    }

    private static JsonObject MergeResourceTemplateMeta(
        JsonObject? meta,
        string sourceId,
        string resourceName,
        string originalUriTemplate
    )
    {
        meta ??= new JsonObject();
        meta[McpGatewayMcpProtocolConstants.ResourceNameMetaPropertyName] = resourceName;
        meta[McpGatewayMcpProtocolConstants.SourceIdMetaPropertyName] = sourceId;
        meta[McpGatewayMcpProtocolConstants.OriginalUriTemplateMetaPropertyName] =
            originalUriTemplate;
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
