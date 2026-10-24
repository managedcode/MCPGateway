namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<McpGatewayStagedCatalog> StageCatalogAsync(McpGatewayCatalogSourceSnapshot source, CancellationToken cancellationToken)
    {
        var diagnostics = new List<McpGatewayDiagnostic>();
        var catalog = await BuildCatalogEntriesAsync(source, diagnostics, cancellationToken);
        var entries = catalog.Entries;
        if (diagnostics.Any(diagnostic => diagnostic.Code == SourceLoadFailedDiagnosticCode))
        {
            throw new InvalidOperationException("The candidate source catalog could not be loaded; the published runtime was preserved.");
        }
        foreach (var entry in entries)
        {
            _ = McpGatewaySchemaValidator.Compile(entry.Descriptor.InputSchema);
            if (entry.Descriptor.OutputSchema is { } output)
            {
                _ = McpGatewaySchemaValidator.Compile(output);
            }
        }
        var vectors = await VectorizeEntriesAsync(entries, diagnostics, cancellationToken);
        var graph = await BuildStagedGraphAsync(entries, diagnostics, cancellationToken);
        var snapshot = CreateSnapshot(source, entries, vectors.IsVectorSearchEnabled, graph) with { LoadedTools = catalog.LoadedTools };
        if (entries.Count > 0 && ShouldBuildGraphSearchIndex() && !IsPublicationGraphValid(snapshot))
        {
            await snapshot.Ownership.RetireAsync(keepForReuse: false);
            throw new InvalidOperationException("The candidate graph index is invalid; the published runtime was preserved.");
        }
        return new(() => TryUpdateState(snapshot, source.Version), () => snapshot.Ownership.RetireAsync(keepForReuse: false));
    }

    private Task<ToolGraphSearchIndex?> BuildStagedGraphAsync(IReadOnlyList<ToolCatalogEntry> entries,
        IList<McpGatewayDiagnostic> diagnostics, CancellationToken cancellationToken) =>
        entries.Count == 0 || !ShouldBuildGraphSearchIndex() ? Task.FromResult<ToolGraphSearchIndex?>(null)
            : TryBuildGraphIndexAsync(entries, diagnostics, cancellationToken);
}
