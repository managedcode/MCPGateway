using ManagedCode.MarkdownLd.Kb.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<VisibleSnapshot> GetVisibleSnapshotAsync(CancellationToken cancellationToken, McpGatewaySearchRequest? request = null)
    {
        if (_managedCatalog && (await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken)).Definitions.IsEmpty)
        {
            return new(ToolCatalogSnapshot.Empty, null);
        }
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var lease = snapshot.Ownership.TryAcquire();
        while (lease is null)
        {
            snapshot = await GetSnapshotAsync(cancellationToken);
            lease = snapshot.Ownership.TryAcquire();
        }
        KnowledgeGraph? ownedGraph = null;
        try
        {
            var policy = McpGatewayTrustedContext.OperationServices(_serviceProvider).GetService<IMcpGatewayAccessPolicy>();
            if (policy is null && !_managedCatalog && request?.SourceId is null && request?.Category is null)
            {
                return new(snapshot, null, lease);
            }
            var context = await McpGatewayTrustedContext.ResolveAsync(_serviceProvider, cancellationToken);
            var publication = _managedCatalog
                ? await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken)
                : null;
            var published = publication?.Definitions.ToDictionary(definition => definition.ToolId, StringComparer.OrdinalIgnoreCase);
            var candidates = new List<ToolCatalogEntry>();
            foreach (var entry in snapshot.Entries)
            {
                if (request?.SourceId is { } sourceId && !entry.Descriptor.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (request?.Category is { } category && !entry.Descriptor.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (published is not null && (!published.TryGetValue(entry.Descriptor.ToolId, out var definition)
                    || definition.Lifecycle != McpGatewayToolLifecycle.Published
                    || publication!.RevokedToolIds.Contains(entry.Descriptor.ToolId)
                    || !McpGatewayCatalogPublicationService.IsMatching(definition, entry.Descriptor)))
                {
                    continue;
                }
                if (published is not null)
                {
                    var approved = published[entry.Descriptor.ToolId];
                    candidates.Add(entry with
                    {
                        Descriptor = entry.Descriptor with
                        { RequiredPermissions = approved.RequiredPermissions, DefinitionHash = approved.DefinitionHash }
                    });
                }
                else
                {
                    candidates.Add(entry);
                }
            }
            var descriptors = candidates.Select(entry => entry.Descriptor).ToArray();
            var allowed = !context.IsAuthenticated ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : policy is null ? descriptors.Select(tool => tool.ToolId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : await policy.FilterDiscoveryAsync(context, descriptors, cancellationToken);
            var visible = candidates.Where(entry => allowed.Contains(entry.Descriptor.ToolId)).ToArray();
            var fingerprint = McpGatewayDefinitionHash.Compute(new
            {
                context.SubjectId,
                context.TenantId,
                context.AuthorizationPartition,
                context.PolicyRevision,
                publication?.Revision,
                snapshot.Version,
                Tools = visible.Select(entry => entry.Descriptor.ToolId).ToArray()
            });
            var graphIndex = snapshot.GraphIndex;
            if (graphIndex is not null && _markdownLdGraphSource != McpGatewayMarkdownLdGraphSource.GeneratedToolGraph)
            {
                var factory = McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayAuthorizedGraphFactory>();
                ownedGraph = await factory.CreateAsync(context, graphIndex.Graph, visible.Select(entry => entry.Descriptor).ToArray(), cancellationToken);
                if (ReferenceEquals(ownedGraph, graphIndex.Graph))
                {
                    throw new InvalidOperationException("Authorized graph factories must return an independently owned projected graph.");
                }
                graphIndex = CreateToolGraphSearchIndex(ownedGraph,
                    snapshot.GraphIndex!.EntriesByNodeId.Where(pair => allowed.Contains(pair.Value.Descriptor.ToolId)).ToDictionary(pair => pair.Key, pair => pair.Value));
            }
            else if (graphIndex is not null && visible.Length != snapshot.Entries.Count)
            {
                graphIndex = visible.Length == 0 ? null : await BuildToolGraphSearchIndexAsync(visible, new List<McpGatewayDiagnostic>(), cancellationToken);
                ownedGraph = graphIndex?.Graph;
            }
            var owner = ownedGraph is null ? null : new SnapshotOwnership(graphIndex);
            if (owner is not null)
            { _ownedSnapshots.TryAdd(owner, 0); }
            return new(snapshot with { Entries = visible, GraphIndex = graphIndex, VisibilityFingerprint = fingerprint }, owner, lease, this);
        }
        catch
        {
            ownedGraph?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private sealed class VisibleSnapshot(ToolCatalogSnapshot snapshot, SnapshotOwnership? ownedGraph, IDisposable? lease = null, McpGatewayRuntime? runtime = null) : IDisposable
    {
        public ToolCatalogSnapshot Snapshot { get; } = snapshot;
        public void Dispose()
        {
            try
            { if (ownedGraph is not null) { _ = runtime!.RetireSnapshotAsync(ownedGraph); } }
            finally { lease?.Dispose(); }
        }
    }
}
