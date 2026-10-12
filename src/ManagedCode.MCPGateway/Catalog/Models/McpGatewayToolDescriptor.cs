using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolDescriptor(
    string ToolId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    Tool ProtocolTool,
    IReadOnlyList<string> RequiredArguments
)
{
    /// <summary>Stable source destination identity used by reviewed publication, never credential values.</summary>
    public string? BindingFingerprint { get; init; }
    /// <summary>Original source name retained separately from the canonical gateway protocol name.</summary>
    public string? UpstreamToolName { get; init; }
    public bool RequiresPublication { get; init; }
    public string? DefinitionHash { get; init; }
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];
    public string ToolName => ProtocolTool.Name;

    public string? DisplayName => ProtocolTool.Title;

    public string Description => ProtocolTool.Description ?? string.Empty;

    public JsonElement InputSchema => ProtocolTool.InputSchema;

    public JsonElement? OutputSchema => ProtocolTool.OutputSchema;

    public ToolAnnotations? Annotations => ProtocolTool.Annotations;

    public JsonObject? Meta => ProtocolTool.Meta;

    public bool? IsReadOnly => ProtocolTool.Annotations?.ReadOnlyHint;

    public bool? IsIdempotent => ProtocolTool.Annotations?.IdempotentHint;

    public bool? IsDestructive => ProtocolTool.Annotations?.DestructiveHint;

    public bool? IsOpenWorld => ProtocolTool.Annotations?.OpenWorldHint;

    public IReadOnlyList<string> SearchAliases { get; init; } = [];

    public IReadOnlyList<string> SearchKeywords { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> DataSources { get; init; } = [];

    public IReadOnlyList<McpGatewayToolExample> UsageExamples { get; init; } = [];

    public McpGatewayToolCostTier? CostTier { get; init; }

    public McpGatewayToolLatencyTier? LatencyTier { get; init; }

    public bool IsEnabledByDefault { get; init; } = true;
}
