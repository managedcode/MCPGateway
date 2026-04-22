namespace ManagedCode.MCPGateway;

public sealed record McpGatewayResourceDescriptor(
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string ResourceName,
    string? DisplayName,
    string ResourceUri,
    string Description,
    string? MimeType,
    long? Size
);
