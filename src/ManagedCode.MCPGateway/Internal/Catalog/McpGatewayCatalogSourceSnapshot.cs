namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayCatalogSourceSnapshot(
    int Version,
    IReadOnlyList<McpGatewayToolSourceRegistration> Registrations);
