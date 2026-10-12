using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayCatalogPublicationService(IMcpGatewayCatalogInspection inspection,
    IServiceProvider services) : IMcpGatewayCatalogPublication
{
    private const string CatalogAsset = "catalog";
    private const string RevisionConflict = "publication_revision_conflict";
    private const string StoreRejected = "publication_store_rejected";
    private const string InvalidDefinition = "publication_definition_invalid";
    private const string Added = "added";
    private const string Changed = "changed";
    private const string Removed = "removed";

    public async Task<IReadOnlyList<McpGatewayCatalogDefinition>> InspectCandidateAsync(CancellationToken cancellationToken = default)
    {
        await McpGatewayAccessBoundary.DemandAsync(services, McpGatewayOperation.ManageCatalog, CatalogAsset, null, cancellationToken);
        var tools = await inspection.InspectCandidateToolsAsync(cancellationToken);
        return tools.Select(tool => McpGatewayDefinitionHash.Create(tool, BindingFingerprint(tool))).ToArray();
    }

    public async Task<McpGatewayPublicationResult> PublishAsync(IReadOnlyList<McpGatewayCatalogDefinition> definitions, long expectedRevision,
        string reviewerReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerReference);
        await McpGatewayAccessBoundary.DemandAsync(services, McpGatewayOperation.ManageCatalog, CatalogAsset, null, cancellationToken);
        var store = McpGatewayTrustedContext.OperationServices(services).GetRequiredService<IMcpGatewayCatalogDefinitionStore>();
        var current = await store.ReadAsync(cancellationToken);
        if (current.Revision != expectedRevision)
        {
            var conflict = new McpGatewayPublicationResult(false, current.Revision, []) { ErrorCode = RevisionConflict };
            McpGatewayInvocationTelemetry.Publication(conflict);
            return conflict;
        }
        var candidateTools = (await inspection.InspectCandidateToolsAsync(cancellationToken)).ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase);
        var approved = ImmutableArray.CreateBuilder<McpGatewayCatalogDefinition>(definitions.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new HashSet<(string SourceId, string? UpstreamName)>();
        foreach (var definition in definitions)
        {
            if (!ids.Add(definition.ToolId) || !bindings.Add((definition.SourceId.ToLowerInvariant(), definition.UpstreamToolName))
                || !candidateTools.TryGetValue(definition.ToolId, out var tool)
                || !IsMatching(definition, tool) || definition.Lifecycle is not (McpGatewayToolLifecycle.Approved or McpGatewayToolLifecycle.Published))
            {
                var invalid = new McpGatewayPublicationResult(false, current.Revision, []) { ErrorCode = InvalidDefinition };
                McpGatewayInvocationTelemetry.Publication(invalid);
                return invalid;
            }
            _ = McpGatewaySchemaValidator.Compile(tool.InputSchema);
            if (tool.OutputSchema is { } output)
            {
                _ = McpGatewaySchemaValidator.Compile(output);
            }
            approved.Add(definition with { Lifecycle = McpGatewayToolLifecycle.Published, ReviewerReference = reviewerReference, ProtocolTool = definition.ProtocolTool.Clone() });
        }
        var changes = Diff(current.Definitions, approved);
        var published = new McpGatewayPublishedCatalog(current.Revision + 1, approved.ToImmutable(), current.RevokedToolIds);
        var success = await store.CompareExchangeAsync(expectedRevision, published, cancellationToken);
        var revision = success ? published.Revision : (await store.ReadAsync(cancellationToken)).Revision;
        var result = new McpGatewayPublicationResult(success, revision, changes)
        { ErrorCode = success ? null : revision == expectedRevision ? StoreRejected : RevisionConflict };
        McpGatewayInvocationTelemetry.Publication(result);
        return result;
    }

    public async Task<McpGatewayPublicationResult> RevokeAsync(string toolId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        toolId = toolId.ToLowerInvariant();
        await McpGatewayAccessBoundary.DemandAsync(services, McpGatewayOperation.ManageCatalog, CatalogAsset, null, cancellationToken);
        var store = McpGatewayTrustedContext.OperationServices(services).GetRequiredService<IMcpGatewayCatalogDefinitionStore>();
        var current = await store.ReadAsync(cancellationToken);
        var candidate = current with { Revision = current.Revision + 1, RevokedToolIds = current.RevokedToolIds.Add(toolId) };
        var success = current.Revision == expectedRevision && await store.CompareExchangeAsync(expectedRevision, candidate, cancellationToken);
        var revision = success ? candidate.Revision : (await store.ReadAsync(cancellationToken)).Revision;
        var result = new McpGatewayPublicationResult(success, revision, [])
        { ErrorCode = success ? null : revision == expectedRevision ? StoreRejected : RevisionConflict };
        McpGatewayInvocationTelemetry.Publication(result);
        return result;
    }

    internal static bool IsMatching(McpGatewayCatalogDefinition definition, McpGatewayToolDescriptor tool) =>
        definition.ToolId == tool.ToolId
        && definition.SourceId == tool.SourceId
        && definition.UpstreamToolName == tool.UpstreamToolName
        && definition.BindingFingerprint == BindingFingerprint(tool)
        && McpGatewayDefinitionHash.Compute(definition.ProtocolTool) == McpGatewayDefinitionHash.Compute(tool.ProtocolTool)
        && definition.SchemaHash == McpGatewayDefinitionHash.Compute(new { tool.InputSchema, tool.OutputSchema })
        && definition.DefinitionHash == McpGatewayDefinitionHash.ComputeDefinition(definition);

    internal static string BindingFingerprint(McpGatewayToolDescriptor tool) =>
        tool.BindingFingerprint ?? McpGatewayDefinitionHash.Compute(new { tool.SourceId, tool.SourceKind, tool.ToolName });

    private static IReadOnlyList<McpGatewayCatalogChange> Diff(IEnumerable<McpGatewayCatalogDefinition> previous,
        IEnumerable<McpGatewayCatalogDefinition> candidate)
    {
        var old = previous.ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase);
        var changes = new List<McpGatewayCatalogChange>();
        foreach (var definition in candidate)
        {
            if (!old.Remove(definition.ToolId, out var existing))
            {
                changes.Add(new(definition.ToolId, Added, null, definition.DefinitionHash));
            }
            else if (existing.DefinitionHash != definition.DefinitionHash)
            {
                changes.Add(new(definition.ToolId, Changed, existing.DefinitionHash, definition.DefinitionHash));
            }
        }
        foreach (var removed in old.Values)
        {
            changes.Add(new(removed.ToolId, Removed, removed.DefinitionHash, null));
        }
        return changes;
    }
}
