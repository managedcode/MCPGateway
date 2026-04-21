using System.Text.Json;
using System.Text.Json.Nodes;
using ManagedCode.MCPGateway.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerHandlers(
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog
)
{
    private const string ToolIdMetaPropertyName = "toolId";
    private const string ToolNameMetaPropertyName = "toolName";
    private const string CategoriesMetaPropertyName = "categories";
    private const string TagsMetaPropertyName = "tags";
    private const string DataSourcesMetaPropertyName = "dataSources";
    private const string UsageExamplesMetaPropertyName = "usageExamples";
    private const string CostTierMetaPropertyName = "costTier";
    private const string LatencyTierMetaPropertyName = "latencyTier";
    private const string EnabledByDefaultMetaPropertyName = "enabledByDefault";
    private const string PromptIdMetaPropertyName = "promptId";
    private const string PromptNameMetaPropertyName = "promptName";
    private const string SourceIdMetaPropertyName = "sourceId";
    private const string InvalidToolNameMessage = "A tool name is required.";
    private const string InvalidPromptNameMessage = "A prompt name is required.";

    public async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> _,
        CancellationToken cancellationToken
    )
    {
        var descriptors = await gateway.ListToolsAsync(cancellationToken);
        return new ListToolsResult { Tools = descriptors.Select(ToProtocolTool).ToList() };
    }

    public async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var toolId = request.Params?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return CreateErrorToolResult(InvalidToolNameMessage);
        }

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: toolId,
                Arguments: ConvertArguments(request.Params?.Arguments)
            ),
            cancellationToken
        );

        return ToProtocolToolResult(invokeResult);
    }

    public async ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> _,
        CancellationToken cancellationToken
    )
    {
        var descriptors = await promptCatalog.ListPromptsAsync(cancellationToken);
        return new ListPromptsResult { Prompts = descriptors.Select(ToProtocolPrompt).ToList() };
    }

    public async ValueTask<GetPromptResult> GetPromptAsync(
        RequestContext<GetPromptRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var exportedPromptName = request.Params?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(exportedPromptName))
        {
            return CreateErrorPromptResult(InvalidPromptNameMessage);
        }

        var descriptors = await promptCatalog.ListPromptsAsync(cancellationToken);
        var descriptor = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.PromptId, exportedPromptName, StringComparison.Ordinal)
        );
        if (descriptor is null)
        {
            return CreateErrorPromptResult($"Prompt '{exportedPromptName}' was not found.");
        }

        var promptResult = await promptCatalog.GetPromptAsync(
            new McpGatewayPromptRequest(
                SourceId: descriptor.SourceId,
                PromptName: descriptor.PromptName,
                Arguments: ConvertArguments(request.Params?.Arguments)
            ),
            cancellationToken
        );

        return promptResult is null
            ? CreateErrorPromptResult($"Prompt '{exportedPromptName}' was not found.")
            : new GetPromptResult
            {
                Description = promptResult.Description,
                Messages = promptResult
                    .Messages.Select(ToProtocolPromptMessage)
                    .Where(static message => message is not null)
                    .Cast<PromptMessage>()
                    .ToList(),
            };
    }

    private static Tool ToProtocolTool(McpGatewayToolDescriptor descriptor) =>
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
            Meta = CreateToolMeta(descriptor),
        };

    private static Prompt ToProtocolPrompt(McpGatewayPromptDescriptor descriptor) =>
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
                [PromptIdMetaPropertyName] = descriptor.PromptId,
                [PromptNameMetaPropertyName] = descriptor.PromptName,
                [SourceIdMetaPropertyName] = descriptor.SourceId,
            },
        };

    private static IReadOnlyDictionary<string, object?>? ConvertArguments(
        IDictionary<string, JsonElement>? arguments
    ) =>
        arguments?.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value.Clone(),
            StringComparer.Ordinal
        );

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
            [ToolIdMetaPropertyName] = descriptor.ToolId,
            [ToolNameMetaPropertyName] = descriptor.ToolName,
            [SourceIdMetaPropertyName] = descriptor.SourceId,
            [EnabledByDefaultMetaPropertyName] = descriptor.IsEnabledByDefault,
        };

        AddStringArray(meta, CategoriesMetaPropertyName, descriptor.Categories);
        AddStringArray(meta, TagsMetaPropertyName, descriptor.Tags);
        AddStringArray(meta, DataSourcesMetaPropertyName, descriptor.DataSources);

        if (descriptor.CostTier is not null)
        {
            meta[CostTierMetaPropertyName] = descriptor.CostTier.Value.ToString();
        }

        if (descriptor.LatencyTier is not null)
        {
            meta[LatencyTierMetaPropertyName] = descriptor.LatencyTier.Value.ToString();
        }

        if (descriptor.UsageExamples.Count > 0)
        {
            meta[UsageExamplesMetaPropertyName] = new JsonArray(
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

    private static CallToolResult ToProtocolToolResult(McpGatewayInvokeResult invokeResult)
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

    private static CallToolResult CreateErrorToolResult(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static GetPromptResult CreateErrorPromptResult(string message) =>
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

    private static PromptMessage? ToProtocolPromptMessage(McpGatewayPromptMessage message)
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
}
