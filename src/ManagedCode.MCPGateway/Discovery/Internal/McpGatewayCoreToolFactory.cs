using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayCoreToolFactory
{
    private readonly McpGatewayToolSet _owner;
    private readonly Lazy<IReadOnlyList<AITool>> _defaultTools;

    public McpGatewayCoreToolFactory(McpGatewayToolSet owner)
    {
        _owner = owner;
        _defaultTools = new(() => CreateCoreTools(McpGatewayToolSet.DefaultSearchToolName,
            McpGatewayToolSet.DefaultRouteToolName, McpGatewayToolSet.DefaultInvokeToolName));
    }

    public IReadOnlyList<AITool> CreateTools(string searchName, string routeName, string invokeName)
    {
        if (searchName == McpGatewayToolSet.DefaultSearchToolName && routeName == McpGatewayToolSet.DefaultRouteToolName
            && invokeName == McpGatewayToolSet.DefaultInvokeToolName)
        {
            return _defaultTools.Value;
        }
        return CreateCoreTools(searchName, routeName, invokeName);
    }

    private IReadOnlyList<AITool> CreateCoreTools(
        string searchToolName,
        string routeToolName,
        string invokeToolName
    )
    {
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchName = McpGatewayToolSet.CreateToolName(searchToolName, McpGatewayToolSet.DefaultSearchToolName, reservedNames);
        var routeName = McpGatewayToolSet.CreateToolName(routeToolName, McpGatewayToolSet.DefaultRouteToolName, reservedNames);
        var invokeName = McpGatewayToolSet.CreateToolName(invokeToolName, McpGatewayToolSet.DefaultInvokeToolName, reservedNames);

        var searchTool = AIFunctionFactory.Create(
            _owner.SearchAsync,
            new AIFunctionFactoryOptions
            {
                Name = searchName,
                Description = McpGatewayToolSet.SearchToolDescription,
            }
        );

        var routeTool = AIFunctionFactory.Create(
            _owner.RouteAsync,
            new AIFunctionFactoryOptions
            {
                Name = routeName,
                Description = McpGatewayToolSet.RouteToolDescription,
            }
        );

        var invokeTool = AIFunctionFactory.Create(
            _owner.InvokeWithResponseAsync,
            new AIFunctionFactoryOptions
            {
                Name = invokeName,
                Description = McpGatewayToolSet.InvokeToolDescription,
            }
        );

        return [searchTool, routeTool, invokeTool];
    }

}
