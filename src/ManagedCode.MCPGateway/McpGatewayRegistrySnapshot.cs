namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayRegistrySnapshot(
    int Version,
    IReadOnlyList<McpGatewayToolSourceRegistration> Registrations);
