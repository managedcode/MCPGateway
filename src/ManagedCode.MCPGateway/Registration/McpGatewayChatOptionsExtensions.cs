using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

public static class McpGatewayChatOptionsExtensions
{
    public static ChatOptions AddMcpGatewayTools(
        this ChatOptions options,
        McpGatewayToolSet toolSet,
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toolSet);

        options.Tools = toolSet.AddTools(options.Tools ?? new List<AITool>(), searchToolName, invokeToolName);
        return options;
    }

    public static ChatOptions AddMcpGatewayTools(
        this ChatOptions options,
        IServiceProvider serviceProvider,
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return options.AddMcpGatewayTools(
            serviceProvider.GetRequiredService<McpGatewayToolSet>(),
            searchToolName,
            invokeToolName);
    }
}
