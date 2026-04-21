using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayToolSet(IMcpGateway gateway)
{
    public const string DefaultSearchToolName = "gateway_tools_search";
    public const string DefaultRouteToolName = "gateway_tools_route";
    public const string DefaultInvokeToolName = "gateway_tool_invoke";
    public const string DiscoveredToolIdPropertyName = "ManagedCode.MCPGateway.ToolId";
    public const string DiscoveredToolSourceIdPropertyName = "ManagedCode.MCPGateway.SourceId";
    public const string DiscoveredToolKindPropertyName = "ManagedCode.MCPGateway.Kind";
    public const string SearchToolDescription =
        "Search the gateway catalog and return the best matching tools for a user task.";
    public const string RouteToolDescription =
        "Route a task into the best tool categories first, then return the strongest tools per category.";
    public const string InvokeToolDescription =
        "Invoke a gateway tool by tool id. Search first when the correct tool is unknown.";
    private const string DiscoveredToolKindValue = "gateway_discovered_tool";

    public IReadOnlyList<AITool> CreateTools(
        string searchToolName = DefaultSearchToolName,
        string routeToolName = DefaultRouteToolName,
        string invokeToolName = DefaultInvokeToolName
    )
    {
        var searchTool = AIFunctionFactory.Create(
            SearchAsync,
            new AIFunctionFactoryOptions
            {
                Name = searchToolName,
                Description = SearchToolDescription,
            }
        );

        var routeTool = AIFunctionFactory.Create(
            RouteAsync,
            new AIFunctionFactoryOptions
            {
                Name = routeToolName,
                Description = RouteToolDescription,
            }
        );

        var invokeTool = AIFunctionFactory.Create(
            InvokeAsync,
            new AIFunctionFactoryOptions
            {
                Name = invokeToolName,
                Description = InvokeToolDescription,
            }
        );

        return [searchTool, routeTool, invokeTool];
    }

    public IList<AITool> AddTools(
        IList<AITool> tools,
        string searchToolName = DefaultSearchToolName,
        string routeToolName = DefaultRouteToolName,
        string invokeToolName = DefaultInvokeToolName
    )
    {
        ArgumentNullException.ThrowIfNull(tools);

        var targetTools = new List<AITool>(tools);
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in targetTools)
        {
            toolNames.Add(tool.Name);
        }

        foreach (var tool in CreateTools(searchToolName, routeToolName, invokeToolName))
        {
            if (toolNames.Add(tool.Name))
            {
                targetTools.Add(tool);
            }
        }

        return targetTools;
    }

    public IReadOnlyList<AITool> CreateDiscoveredTools(
        IEnumerable<McpGatewaySearchMatch> matches,
        IReadOnlyCollection<string>? reservedToolNames = null,
        int? maxTools = null
    )
    {
        ArgumentNullException.ThrowIfNull(matches);

        var toolLimit = maxTools.GetValueOrDefault(int.MaxValue);
        if (toolLimit <= 0)
        {
            return [];
        }

        var discoveredTools = new List<AITool>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (reservedToolNames is not null)
        {
            foreach (var reservedToolName in reservedToolNames)
            {
                if (!string.IsNullOrWhiteSpace(reservedToolName))
                {
                    reservedNames.Add(reservedToolName);
                }
            }
        }

        foreach (var match in matches)
        {
            if (discoveredTools.Count == toolLimit)
            {
                break;
            }

            var functionName = McpGatewayDiscoveredToolNaming.CreateName(match, reservedNames);
            discoveredTools.Add(CreateDiscoveredTool(match, functionName));
        }

        return discoveredTools;
    }

    public Task<McpGatewaySearchResult> SearchAsync(
        string query,
        int? maxResults = null,
        Dictionary<string, object?>? context = null,
        string? contextSummary = null,
        CancellationToken cancellationToken = default
    ) =>
        gateway.SearchAsync(
            new McpGatewaySearchRequest(
                Query: query,
                MaxResults: maxResults,
                Context: context,
                ContextSummary: contextSummary
            ),
            cancellationToken
        );

    public Task<McpGatewayInvokeResult> InvokeAsync(
        string toolId,
        Dictionary<string, object?>? arguments = null,
        string? query = null,
        Dictionary<string, object?>? context = null,
        string? contextSummary = null,
        CancellationToken cancellationToken = default
    ) =>
        gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: toolId,
                Arguments: arguments,
                Query: query,
                Context: context,
                ContextSummary: contextSummary
            ),
            cancellationToken
        );

    public Task<McpGatewayToolRouteResult> RouteAsync(
        string query,
        int? maxCategories = null,
        int? maxToolsPerCategory = null,
        Dictionary<string, object?>? context = null,
        string? contextSummary = null,
        bool? preferReadOnly = null,
        bool includeDisabledTools = false,
        CancellationToken cancellationToken = default
    ) =>
        gateway.RouteToolsAsync(
            new McpGatewayToolRouteRequest(
                Query: query,
                MaxCategories: maxCategories,
                MaxToolsPerCategory: maxToolsPerCategory,
                Context: context,
                ContextSummary: contextSummary,
                PreferReadOnly: preferReadOnly,
                IncludeDisabledTools: includeDisabledTools
            ),
            cancellationToken
        );

    private AITool CreateDiscoveredTool(McpGatewaySearchMatch match, string functionName)
    {
        Task<McpGatewayInvokeResult> InvokeDiscoveredToolAsync(
            Dictionary<string, object?>? arguments = null,
            string? query = null,
            Dictionary<string, object?>? context = null,
            string? contextSummary = null,
            CancellationToken cancellationToken = default
        ) =>
            gateway.InvokeAsync(
                new McpGatewayInvokeRequest(
                    ToolId: match.ToolId,
                    Arguments: arguments,
                    Query: query,
                    Context: context,
                    ContextSummary: contextSummary
                ),
                cancellationToken
            );

        return AIFunctionFactory.Create(
            (Func<
                Dictionary<string, object?>?,
                string?,
                Dictionary<string, object?>?,
                string?,
                CancellationToken,
                Task<McpGatewayInvokeResult>
            >)
                InvokeDiscoveredToolAsync,
            new AIFunctionFactoryOptions
            {
                Name = functionName,
                Description = McpGatewayDiscoveredToolNaming.BuildDescription(match),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [DiscoveredToolIdPropertyName] = match.ToolId,
                    [DiscoveredToolSourceIdPropertyName] = match.SourceId,
                    [DiscoveredToolKindPropertyName] = DiscoveredToolKindValue,
                },
            }
        );
    }
}
