using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

public static class McpGatewayChatOptionsExtensions
{
    public static ChatOptions AddMcpGatewayTools(
        this ChatOptions options,
        McpGatewayToolSet toolSet,
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toolSet);

        options.Tools = toolSet.AddTools(
            options.Tools ?? new List<AITool>(),
            searchToolName,
            routeToolName,
            invokeToolName
        );
        return options;
    }

    public static ChatOptions AddMcpGatewayTools(
        this ChatOptions options,
        IServiceProvider serviceProvider,
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return options.AddMcpGatewayTools(
            serviceProvider.GetRequiredService<McpGatewayToolSet>(),
            searchToolName,
            routeToolName,
            invokeToolName
        );
    }

    public static ChatOptions AddMcpGatewayGraphTools(
        this ChatOptions options,
        McpGatewayToolSet toolSet,
        string graphSearchToolName = McpGatewayToolSet.DefaultGraphSearchToolName,
        string graphFederatedSearchToolName = McpGatewayToolSet.DefaultGraphFederatedSearchToolName,
        string graphExportToolName = McpGatewayToolSet.DefaultGraphExportToolName,
        string graphSchemaToolName = McpGatewayToolSet.DefaultGraphSchemaToolName,
        string toolIndexBuildToolName = McpGatewayToolSet.DefaultToolIndexBuildToolName
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toolSet);

        options.Tools = toolSet.AddGraphTools(
            options.Tools ?? new List<AITool>(),
            graphSearchToolName,
            graphFederatedSearchToolName,
            graphExportToolName,
            graphSchemaToolName,
            toolIndexBuildToolName
        );
        return options;
    }

    public static ChatOptions AddMcpGatewayGraphTools(
        this ChatOptions options,
        IServiceProvider serviceProvider,
        string graphSearchToolName = McpGatewayToolSet.DefaultGraphSearchToolName,
        string graphFederatedSearchToolName = McpGatewayToolSet.DefaultGraphFederatedSearchToolName,
        string graphExportToolName = McpGatewayToolSet.DefaultGraphExportToolName,
        string graphSchemaToolName = McpGatewayToolSet.DefaultGraphSchemaToolName,
        string toolIndexBuildToolName = McpGatewayToolSet.DefaultToolIndexBuildToolName
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return options.AddMcpGatewayGraphTools(
            serviceProvider.GetRequiredService<McpGatewayToolSet>(),
            graphSearchToolName,
            graphFederatedSearchToolName,
            graphExportToolName,
            graphSchemaToolName,
            toolIndexBuildToolName
        );
    }
}
