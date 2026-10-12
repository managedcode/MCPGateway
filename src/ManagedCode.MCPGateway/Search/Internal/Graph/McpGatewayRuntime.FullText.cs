using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private const string FullTextAsset = "full-text-index";

    public async Task<McpGatewayFullTextIndexResult> BuildFullTextIndexAsync(CancellationToken cancellationToken)
    {
        await McpGatewayAccessBoundary.DemandAsync(_serviceProvider, McpGatewayOperation.AdministerGraph,
            FullTextAsset, null, cancellationToken);
        using var visible = await GetVisibleSnapshotAsync(cancellationToken);
        if (visible.Snapshot.GraphIndex is not { } graph)
        {
            return new(0, 0);
        }
        _ = await graph.FullText.Value.WaitAsync(cancellationToken);
        return new(graph.NodeCount, graph.Graph.TripleCount);
    }

    public async Task<IReadOnlyList<McpGatewayFullTextMatch>> SearchFullTextAsync(string query,
        int? maxResults, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await McpGatewayAccessBoundary.DemandAsync(_serviceProvider, McpGatewayOperation.ReadGraph,
            FullTextAsset, null, cancellationToken);
        using var visible = await GetVisibleSnapshotAsync(cancellationToken);
        if (visible.Snapshot.GraphIndex is not { } graph)
        {
            return [];
        }
        var index = await graph.FullText.Value.WaitAsync(cancellationToken);
        var matches = await index.SearchAsync(query, Math.Clamp(maxResults ?? _defaultSearchLimit, 1, _maxSearchResults), cancellationToken: cancellationToken);
        var result = new List<McpGatewayFullTextMatch>(matches.Count);
        foreach (var match in matches)
        {
            graph.EntriesByNodeId.TryGetValue(match.NodeId, out var entry);
            result.Add(new(match.NodeId, match.Label, match.Score, entry?.Descriptor.ToolId));
        }
        return result;
    }
}
