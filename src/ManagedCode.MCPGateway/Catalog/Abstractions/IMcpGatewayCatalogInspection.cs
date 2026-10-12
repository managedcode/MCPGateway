namespace ManagedCode.MCPGateway;

/// <summary>Privileged raw candidate inspection before publication; unavailable to discovery tools.</summary>
public interface IMcpGatewayCatalogInspection
{
    Task<IReadOnlyList<McpGatewayToolDescriptor>> InspectCandidateToolsAsync(CancellationToken cancellationToken = default);
}
