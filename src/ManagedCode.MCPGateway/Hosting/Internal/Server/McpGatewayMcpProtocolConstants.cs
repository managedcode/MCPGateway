namespace ManagedCode.MCPGateway;

internal static class McpGatewayMcpProtocolConstants
{
    public const string ToolIdMetaPropertyName = "toolId";
    public const string ToolNameMetaPropertyName = "toolName";
    public const string CategoriesMetaPropertyName = "categories";
    public const string TagsMetaPropertyName = "tags";
    public const string DataSourcesMetaPropertyName = "dataSources";
    public const string UsageExamplesMetaPropertyName = "usageExamples";
    public const string CostTierMetaPropertyName = "costTier";
    public const string LatencyTierMetaPropertyName = "latencyTier";
    public const string EnabledByDefaultMetaPropertyName = "enabledByDefault";
    public const string PromptIdMetaPropertyName = "promptId";
    public const string PromptNameMetaPropertyName = "promptName";
    public const string ResourceNameMetaPropertyName = "resourceName";
    public const string OriginalUriMetaPropertyName = "originalUri";
    public const string OriginalUriTemplateMetaPropertyName = "originalUriTemplate";
    public const string SourceIdMetaPropertyName = "sourceId";
    public const string InvalidToolNameMessage = "A tool name is required.";
    public const string InvalidPromptNameMessage = "A prompt name is required.";
    public const string InvalidResourceUriMessage = "A resource URI is required.";
    public const string InvalidCompletionReferenceMessage = "A completion reference is required.";
    public const string InvalidCompletionArgumentMessage = "A completion argument name is required.";
    public const string InvalidTaskMetadataMessage =
        "Task-augmented tools/call requests require task metadata.";
}
