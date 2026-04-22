using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayResourceResult(
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string ResourceUri,
    IReadOnlyList<ResourceContents> Contents
);
