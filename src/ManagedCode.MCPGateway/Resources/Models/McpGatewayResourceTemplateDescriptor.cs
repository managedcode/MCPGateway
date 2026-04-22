namespace ManagedCode.MCPGateway;

public sealed record McpGatewayResourceTemplateDescriptor(
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string ResourceName,
    string? DisplayName,
    string UriTemplate,
    string Description,
    string? MimeType
);
