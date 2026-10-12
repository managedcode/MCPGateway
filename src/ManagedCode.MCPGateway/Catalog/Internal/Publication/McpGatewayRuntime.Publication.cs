using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private readonly bool _managedCatalog;
    private const string CatalogAssetName = "catalog";

    public async Task<IReadOnlyList<McpGatewayToolDescriptor>> InspectCandidateToolsAsync(CancellationToken cancellationToken)
    {
        await McpGatewayAccessBoundary.DemandAsync(_serviceProvider, McpGatewayOperation.ManageCatalog, CatalogAssetName, null, cancellationToken);
        using var lease = await AcquireCatalogLeaseAsync(cancellationToken);
        var snapshot = await GetSnapshotAsync(cancellationToken);
        using var graphLease = snapshot.Ownership.TryAcquire()
            ?? throw new InvalidOperationException("The candidate catalog changed during inspection.");
        if (snapshot.Entries.Count > 0 && ShouldBuildGraphSearchIndex() && !IsPublicationGraphValid(snapshot))
        {
            throw new InvalidOperationException("Catalog publication requires a successfully built and validated graph index.");
        }
        return snapshot.Entries.Select(entry => entry.Descriptor).ToArray();
    }

    private bool IsPublicationGraphValid(ToolCatalogSnapshot snapshot)
    {
        if (snapshot.GraphIndex is not { } graph || graph.EntriesByNodeId.Count != snapshot.Entries.Count)
        {
            return false;
        }
        if (graph.EntriesByNodeId.Keys.Any(nodeId => !graph.NodesById.ContainsKey(nodeId)))
        { return false; }
        var profile = CreateToolGraphSchemaSearchProfile(_defaultSearchLimit,
            GraphFocusedRelatedResultsLimit, GraphFocusedNextStepResultsLimit);
        if (_graphSchemaSearchProfile is null)
        {
            // Descriptions, tags and navigation are optional MCP metadata. Names and RDF types are mandatory.
            profile = profile with
            {
                TextPredicates = profile.TextPredicates.Where(predicate => predicate.Predicate is SchemaPredicateName or SkosPredicatePrefLabel).ToArray(),
                RelationshipPredicates = [],
                ExpansionPredicates = [],
            };
        }
        return graph.Graph.ValidateSchemaSearchProfile(profile).IsValid;
    }

    private async ValueTask<bool> IsPublishedAsync(McpGatewayToolDescriptor tool, CancellationToken cancellationToken)
    {
        if (!_managedCatalog)
        {
            return true;
        }
        var catalog = await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
        return !catalog.RevokedToolIds.Contains(tool.ToolId) && catalog.Definitions.Any(definition =>
            definition.ToolId.Equals(tool.ToolId, StringComparison.OrdinalIgnoreCase)
            && definition.Lifecycle == McpGatewayToolLifecycle.Published
            && McpGatewayCatalogPublicationService.IsMatching(definition, tool));
    }

    private async ValueTask<McpGatewayToolDescriptor> GetApprovedDescriptorAsync(McpGatewayToolDescriptor tool, CancellationToken cancellationToken)
    {
        if (!_managedCatalog)
        {
            return tool with
            {
                DefinitionHash = McpGatewayDefinitionHash.Create(tool,
                tool.BindingFingerprint ?? string.Empty).DefinitionHash
            };
        }
        var catalog = await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
        var definition = catalog.Definitions.FirstOrDefault(item => item.ToolId.Equals(tool.ToolId, StringComparison.OrdinalIgnoreCase));
        return definition is null ? tool : tool with
        {
            DefinitionHash = definition.DefinitionHash,
            RequiredPermissions = definition.RequiredPermissions,
            RequiresPublication = true
        };
    }
}
