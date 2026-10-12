using System.Collections.Immutable;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public enum McpGatewayToolLifecycle
{
    Draft,
    Approved,
    Published,
    Disabled,
    Deprecated,
    Quarantined,
}

/// <summary>Immutable approved protocol data and binding references; never credential values.</summary>
public sealed record McpGatewayCatalogDefinition(
    string ToolId,
    string SourceId,
    JsonElement ProtocolTool,
    string BindingFingerprint,
    string SchemaHash,
    string DefinitionHash
)
{
    public McpGatewayToolLifecycle Lifecycle { get; init; } = McpGatewayToolLifecycle.Draft;
    public ImmutableArray<string> RequiredPermissions { get; init; } = [];
    public string? ReviewerReference { get; init; }
    public string? Provenance { get; init; }
    public string? UpstreamToolName { get; init; }
    public McpGatewayCatalogDefinition WithReviewMetadata(IEnumerable<string> requiredPermissions, string? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(requiredPermissions);
        var candidate = this with
        {
            RequiredPermissions = requiredPermissions.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToImmutableArray(),
            Provenance = provenance
        };
        return candidate with { DefinitionHash = McpGatewayDefinitionHash.ComputeDefinition(candidate) };
    }
    public Tool GetProtocolTool() => ProtocolTool.Deserialize<Tool>(McpGatewayJsonSerializer.Options)
        ?? throw new JsonException("The catalog protocol tool is invalid.");
}

public sealed record McpGatewayPublishedCatalog(long Revision, ImmutableArray<McpGatewayCatalogDefinition> Definitions,
    ImmutableHashSet<string> RevokedToolIds)
{
    public static McpGatewayPublishedCatalog Empty { get; } = new(0, [], ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record McpGatewayCatalogChange(string ToolId, string Kind, string? PreviousHash, string? CandidateHash);

public sealed record McpGatewayPublicationResult(bool IsPublished, long Revision, IReadOnlyList<McpGatewayCatalogChange> Changes)
{
    public string? ErrorCode { get; init; }
}
