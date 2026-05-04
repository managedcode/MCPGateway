using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayToolSet
{
    public const string DefaultSearchToolName = "gateway_tools_search";
    public const string DefaultRouteToolName = "gateway_tools_route";
    public const string DefaultInvokeToolName = "gateway_tool_invoke";
    public const string DefaultGraphSearchToolName = "gateway_graph_schema_search";
    public const string DefaultGraphFederatedSearchToolName = "gateway_graph_federated_search";
    public const string DefaultGraphExportToolName = "gateway_graph_export";
    public const string DefaultGraphSchemaToolName = "gateway_graph_schema_describe";
    public const string DefaultToolIndexBuildToolName = "gateway_tool_index_build";
    public const string DiscoveredToolIdPropertyName = "ManagedCode.MCPGateway.ToolId";
    public const string DiscoveredToolSourceIdPropertyName = "ManagedCode.MCPGateway.SourceId";
    public const string DiscoveredToolKindPropertyName = "ManagedCode.MCPGateway.Kind";
    public const string SearchToolDescription =
        "Search the gateway catalog and return the best matching tools for a user task.";
    public const string RouteToolDescription =
        "Route a task into the best tool categories first, then return the strongest tools per category.";
    public const string InvokeToolDescription =
        "Invoke a gateway tool by tool id. Search first when the correct tool is unknown.";
    public const string GraphSearchToolDescription =
        "Run schema-aware Markdown-LD graph search and return graph matches, evidence, generated SPARQL, and mapped gateway tools.";
    public const string GraphFederatedSearchToolDescription =
        "Run explicit allowlisted federated Markdown-LD graph search across the gateway graph and configured SPARQL services.";
    public const string GraphExportToolDescription =
        "Export the current Markdown-LD gateway graph as JSON-LD, Turtle, Mermaid, and DOT artifacts.";
    public const string GraphSchemaToolDescription =
        "Describe the schema-aware Markdown-LD graph profile used to compile SPARQL and validate the current tool graph.";
    public const string ToolIndexBuildToolDescription =
        "Build or rebuild the gateway tool index and return graph, vector, and diagnostic state.";
    private const string DiscoveredToolKindValue = "gateway_discovered_tool";
    private readonly IMcpGateway _gateway;
    private readonly McpGatewayGraphToolFactory _graphTools;

    public McpGatewayToolSet(IMcpGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        _gateway = gateway;
        _graphTools = new McpGatewayGraphToolFactory(gateway, gateway as IMcpGatewayGraphSearch);
    }

    public McpGatewayToolSet(IMcpGateway gateway, IMcpGatewayGraphSearch graphSearch)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(graphSearch);

        _gateway = gateway;
        _graphTools = new McpGatewayGraphToolFactory(gateway, graphSearch);
    }

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

    public IReadOnlyList<AITool> CreateGraphTools(
        string graphSearchToolName = DefaultGraphSearchToolName,
        string graphFederatedSearchToolName = DefaultGraphFederatedSearchToolName,
        string graphExportToolName = DefaultGraphExportToolName,
        string graphSchemaToolName = DefaultGraphSchemaToolName,
        string toolIndexBuildToolName = DefaultToolIndexBuildToolName
    ) =>
        _graphTools.CreateGraphTools(
            graphSearchToolName,
            graphFederatedSearchToolName,
            graphExportToolName,
            graphSchemaToolName,
            toolIndexBuildToolName
        );

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

    public IList<AITool> AddGraphTools(
        IList<AITool> tools,
        string graphSearchToolName = DefaultGraphSearchToolName,
        string graphFederatedSearchToolName = DefaultGraphFederatedSearchToolName,
        string graphExportToolName = DefaultGraphExportToolName,
        string graphSchemaToolName = DefaultGraphSchemaToolName,
        string toolIndexBuildToolName = DefaultToolIndexBuildToolName
    ) =>
        _graphTools.AddGraphTools(
            tools,
            graphSearchToolName,
            graphFederatedSearchToolName,
            graphExportToolName,
            graphSchemaToolName,
            toolIndexBuildToolName
        );

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
        _gateway.SearchAsync(
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
        _gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: toolId,
                Arguments: arguments,
                Query: query,
                Context: context,
                ContextSummary: contextSummary
            ),
            cancellationToken
        );

    public Task<McpGatewayGraphSchemaResult> DescribeGraphSchemaAsync(
        CancellationToken cancellationToken = default
    ) => _graphTools.DescribeGraphSchemaAsync(cancellationToken);

    public Task<McpGatewayIndexBuildResult> BuildToolIndexAsync(
        CancellationToken cancellationToken = default
    ) => _graphTools.BuildToolIndexAsync(cancellationToken);

    public Task<McpGatewayGraphSearchResult> SchemaGraphSearchAsync(
        string query,
        int? maxResults = null,
        CancellationToken cancellationToken = default
    ) => _graphTools.SchemaGraphSearchAsync(query, maxResults, cancellationToken);

    public Task<McpGatewayGraphSearchResult> FederatedGraphSearchAsync(
        string query,
        int? maxResults = null,
        IReadOnlyList<string>? serviceEndpoints = null,
        bool includeLocalGatewayGraph = true,
        CancellationToken cancellationToken = default
    ) =>
        _graphTools.FederatedGraphSearchAsync(
            query,
            maxResults,
            serviceEndpoints,
            includeLocalGatewayGraph,
            cancellationToken
        );

    public Task<McpGatewayMarkdownLdGraphExport> ExportGraphAsync(
        CancellationToken cancellationToken = default
    ) => _graphTools.ExportGraphAsync(cancellationToken);

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
        _gateway.RouteToolsAsync(
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
            _gateway.InvokeAsync(
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
