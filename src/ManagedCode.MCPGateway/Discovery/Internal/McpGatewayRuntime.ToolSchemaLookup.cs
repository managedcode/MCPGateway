namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayToolDescriptor?> GetAuthorizedSchemaToolAsync(string toolId, CancellationToken cancellationToken)
    {
        using var lease = await AcquireCatalogLeaseAsync(cancellationToken);
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var resolution = ResolveInvocationTarget(snapshot, new(ToolId: toolId));
        if (resolution.Entry is not { } entry || !await IsPublishedAsync(entry.Descriptor, cancellationToken))
        { return null; }
        var tool = await GetApprovedDescriptorAsync(entry.Descriptor, cancellationToken);
        var context = await McpGatewayTrustedContext.ResolveAsync(_serviceProvider, cancellationToken);
        foreach (var operation in SchemaLookupOperations)
        {
            var decision = await McpGatewayAccessBoundary.AuthorizeAsync(_serviceProvider, context,
                new(operation, tool.ToolId, tool.SourceId) { RequiredPermissions = tool.RequiredPermissions, DefinitionHash = tool.DefinitionHash }, cancellationToken);
            if (!decision.IsAllowed)
            { return null; }
        }
        using var current = _catalogSource.CreateSnapshot();
        return current.Version == lease.Version && ReferenceEquals(current.Owner, lease.Owner)
            && await IsPublishedAsync(tool, cancellationToken) ? tool : null;
    }
    private static readonly McpGatewayOperation[] SchemaLookupOperations = [McpGatewayOperation.Discover, McpGatewayOperation.InspectSchema];
}
