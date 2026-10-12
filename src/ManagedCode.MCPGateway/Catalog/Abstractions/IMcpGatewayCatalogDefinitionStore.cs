namespace ManagedCode.MCPGateway;

/// <summary>Storage-independent atomic publication; implementations must preserve revision conflicts.</summary>
public interface IMcpGatewayCatalogDefinitionStore
{
    ValueTask<McpGatewayPublishedCatalog> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> CompareExchangeAsync(long expectedRevision, McpGatewayPublishedCatalog candidate, CancellationToken cancellationToken = default);
}

public interface IMcpGatewayCatalogPublication
{
    Task<IReadOnlyList<McpGatewayCatalogDefinition>> InspectCandidateAsync(CancellationToken cancellationToken = default);
    Task<McpGatewayPublicationResult> PublishAsync(IReadOnlyList<McpGatewayCatalogDefinition> definitions, long expectedRevision,
        string reviewerReference, CancellationToken cancellationToken = default);
    Task<McpGatewayPublicationResult> RevokeAsync(string toolId, long expectedRevision, CancellationToken cancellationToken = default);
}
