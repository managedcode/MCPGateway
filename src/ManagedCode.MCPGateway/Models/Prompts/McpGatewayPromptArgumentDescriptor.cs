namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptArgumentDescriptor(
    string Name,
    string? DisplayName,
    string Description,
    bool IsRequired
);
