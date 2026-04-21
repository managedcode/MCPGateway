namespace ManagedCode.MCPGateway;

internal static class McpGatewaySourceKindMapper
{
    public static McpGatewaySourceKind Map(McpGatewaySourceRegistrationKind kind) =>
        kind switch
        {
            McpGatewaySourceRegistrationKind.Http => McpGatewaySourceKind.HttpMcp,
            McpGatewaySourceRegistrationKind.Stdio => McpGatewaySourceKind.StdioMcp,
            McpGatewaySourceRegistrationKind.CustomMcpClient =>
                McpGatewaySourceKind.CustomMcpClient,
            _ => McpGatewaySourceKind.Local,
        };
}
