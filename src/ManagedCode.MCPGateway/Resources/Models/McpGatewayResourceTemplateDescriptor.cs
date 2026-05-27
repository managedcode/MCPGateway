using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayResourceTemplateDescriptor(
    string SourceId,
    McpGatewaySourceKind SourceKind,
    ResourceTemplate ProtocolResourceTemplate
)
{
    public string ResourceName => ProtocolResourceTemplate.Name;

    public string? DisplayName => ProtocolResourceTemplate.Title;

    public string UriTemplate => ProtocolResourceTemplate.UriTemplate;

    public string Description => ProtocolResourceTemplate.Description ?? string.Empty;

    public string? MimeType => ProtocolResourceTemplate.MimeType;
}
