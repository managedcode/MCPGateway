namespace ManagedCode.MCPGateway;

public sealed class McpGatewayAutoDiscoveryOptions
{
    public int MaxDiscoveredTools { get; set; } = 8;

    public string SearchToolName { get; set; } = McpGatewayToolSet.DefaultSearchToolName;

    public string RouteToolName { get; set; } = McpGatewayToolSet.DefaultRouteToolName;

    public string InvokeToolName { get; set; } = McpGatewayToolSet.DefaultInvokeToolName;

    internal McpGatewayAutoDiscoveryOptions Clone() =>
        new()
        {
            MaxDiscoveredTools = MaxDiscoveredTools,
            SearchToolName = SearchToolName,
            RouteToolName = RouteToolName,
            InvokeToolName = InvokeToolName,
        };
}
