using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayGraphToolFactory
{
    private const string GraphSearchUnavailableMessage =
        "Graph tools require IMcpGatewayGraphSearch. Register AddMcpGateway(...) or construct McpGatewayToolSet with a graph search service.";

    private readonly IMcpGateway _gateway;
    private readonly IMcpGatewayGraphSearch? _graphSearch;

    public McpGatewayGraphToolFactory(IMcpGateway gateway, IMcpGatewayGraphSearch? graphSearch)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        _gateway = gateway;
        _graphSearch = graphSearch;
    }

    public IReadOnlyList<AITool> CreateGraphTools(
        string graphSearchToolName,
        string graphFederatedSearchToolName,
        string graphExportToolName,
        string graphSchemaToolName,
        string toolIndexBuildToolName
    )
    {
        _ = RequireGraphSearch();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var graphSchemaName = McpGatewayToolSet.CreateToolName(
            graphSchemaToolName,
            McpGatewayToolSet.DefaultGraphSchemaToolName,
            reservedNames
        );
        var toolIndexBuildName = McpGatewayToolSet.CreateToolName(
            toolIndexBuildToolName,
            McpGatewayToolSet.DefaultToolIndexBuildToolName,
            reservedNames
        );
        var graphSearchName = McpGatewayToolSet.CreateToolName(
            graphSearchToolName,
            McpGatewayToolSet.DefaultGraphSearchToolName,
            reservedNames
        );
        var graphFederatedSearchName = McpGatewayToolSet.CreateToolName(
            graphFederatedSearchToolName,
            McpGatewayToolSet.DefaultGraphFederatedSearchToolName,
            reservedNames
        );
        var graphExportName = McpGatewayToolSet.CreateToolName(
            graphExportToolName,
            McpGatewayToolSet.DefaultGraphExportToolName,
            reservedNames
        );

        var graphSchemaTool = AIFunctionFactory.Create(
            DescribeGraphSchemaAsync,
            new AIFunctionFactoryOptions
            {
                Name = graphSchemaName,
                Description = McpGatewayToolSet.GraphSchemaToolDescription,
            }
        );

        var toolIndexBuildTool = AIFunctionFactory.Create(
            BuildToolIndexAsync,
            new AIFunctionFactoryOptions
            {
                Name = toolIndexBuildName,
                Description = McpGatewayToolSet.ToolIndexBuildToolDescription,
            }
        );

        var graphSearchTool = AIFunctionFactory.Create(
            SchemaGraphSearchAsync,
            new AIFunctionFactoryOptions
            {
                Name = graphSearchName,
                Description = McpGatewayToolSet.GraphSearchToolDescription,
            }
        );

        var graphFederatedSearchTool = AIFunctionFactory.Create(
            FederatedGraphSearchAsync,
            new AIFunctionFactoryOptions
            {
                Name = graphFederatedSearchName,
                Description = McpGatewayToolSet.GraphFederatedSearchToolDescription,
            }
        );

        var graphExportTool = AIFunctionFactory.Create(
            ExportGraphAsync,
            new AIFunctionFactoryOptions
            {
                Name = graphExportName,
                Description = McpGatewayToolSet.GraphExportToolDescription,
            }
        );

        return [
            graphSchemaTool,
            toolIndexBuildTool,
            graphSearchTool,
            graphFederatedSearchTool,
            graphExportTool,
        ];
    }

    public IList<AITool> AddGraphTools(
        IList<AITool> tools,
        string graphSearchToolName,
        string graphFederatedSearchToolName,
        string graphExportToolName,
        string graphSchemaToolName,
        string toolIndexBuildToolName
    )
    {
        ArgumentNullException.ThrowIfNull(tools);

        var targetTools = new List<AITool>(tools);
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in targetTools)
        {
            toolNames.Add(tool.Name);
            toolNames.Add(
                McpGatewayToolSet.NormalizeToolName(
                    tool.Name,
                    McpGatewayToolSet.DefaultGraphSearchToolName
                )
            );
        }

        foreach (
            var tool in CreateGraphTools(
                graphSearchToolName,
                graphFederatedSearchToolName,
                graphExportToolName,
                graphSchemaToolName,
                toolIndexBuildToolName
            )
        )
        {
            if (toolNames.Add(tool.Name))
            {
                targetTools.Add(tool);
            }
        }

        return targetTools;
    }

    public Task<McpGatewayGraphSchemaResult> DescribeGraphSchemaAsync(
        CancellationToken cancellationToken = default
    ) => RequireGraphSearch().DescribeGraphSchemaAsync(cancellationToken);

    public Task<McpGatewayIndexBuildResult> BuildToolIndexAsync(
        CancellationToken cancellationToken = default
    ) => _gateway.BuildIndexAsync(cancellationToken);

    public Task<McpGatewayGraphSearchResult> SchemaGraphSearchAsync(
        string query,
        int? maxResults = null,
        CancellationToken cancellationToken = default
    ) =>
        RequireGraphSearch().SearchGraphAsync(
            new McpGatewayGraphSearchRequest(query) { MaxResults = maxResults },
            cancellationToken
        );

    public Task<McpGatewayGraphSearchResult> FederatedGraphSearchAsync(
        string query,
        int? maxResults = null,
        IReadOnlyList<string>? serviceEndpoints = null,
        bool includeLocalGatewayGraph = true,
        CancellationToken cancellationToken = default
    ) =>
        RequireGraphSearch().SearchGraphAsync(
            new McpGatewayGraphSearchRequest(query)
            {
                MaxResults = maxResults,
                UseFederation = true,
                IncludeLocalGatewayGraph = includeLocalGatewayGraph,
                ServiceEndpoints = serviceEndpoints ?? [],
            },
            cancellationToken
        );

    public Task<McpGatewayMarkdownLdGraphExport> ExportGraphAsync(
        CancellationToken cancellationToken = default
    ) => RequireGraphSearch().ExportMarkdownLdGraphAsync(cancellationToken);

    private IMcpGatewayGraphSearch RequireGraphSearch() =>
        _graphSearch ?? throw new NotSupportedException(GraphSearchUnavailableMessage);
}
