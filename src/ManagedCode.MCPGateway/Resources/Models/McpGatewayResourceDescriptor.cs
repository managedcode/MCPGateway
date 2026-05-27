using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayResourceDescriptor(
    string SourceId,
    McpGatewaySourceKind SourceKind,
    Resource ProtocolResource
)
{
    public string ResourceName => ProtocolResource.Name;

    public string? DisplayName => ProtocolResource.Title;

    public string ResourceUri => ProtocolResource.Uri;

    public string Description => ProtocolResource.Description ?? string.Empty;

    public string? MimeType => ProtocolResource.MimeType;

    public long? Size => ProtocolResource.Size;
}
