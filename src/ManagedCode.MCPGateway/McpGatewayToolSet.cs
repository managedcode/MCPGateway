using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayToolSet(IMcpGateway gateway)
{
    public const string DefaultSearchToolName = "gateway_tools_search";
    public const string DefaultInvokeToolName = "gateway_tool_invoke";
    public const string SearchToolDescription = "Search the gateway catalog and return the best matching tools for a user task.";
    public const string InvokeToolDescription = "Invoke a gateway tool by tool id. Search first when the correct tool is unknown.";

    public IReadOnlyList<AITool> CreateTools(
        string searchToolName = DefaultSearchToolName,
        string invokeToolName = DefaultInvokeToolName)
    {
        var searchTool = AIFunctionFactory.Create(
            SearchAsync,
            new AIFunctionFactoryOptions
            {
                Name = searchToolName,
                Description = SearchToolDescription
            });

        var invokeTool = AIFunctionFactory.Create(
            InvokeAsync,
            new AIFunctionFactoryOptions
            {
                Name = invokeToolName,
                Description = InvokeToolDescription
            });

        return [searchTool, invokeTool];
    }

    public Task<McpGatewaySearchResult> SearchAsync(
        string query,
        int? maxResults = null,
        Dictionary<string, object?>? context = null,
        string? contextSummary = null,
        CancellationToken cancellationToken = default)
        => gateway.SearchAsync(
            new McpGatewaySearchRequest(
                Query: query,
                MaxResults: maxResults,
                Context: context,
                ContextSummary: contextSummary),
            cancellationToken);

    public Task<McpGatewayInvokeResult> InvokeAsync(
        string toolId,
        Dictionary<string, object?>? arguments = null,
        string? query = null,
        Dictionary<string, object?>? context = null,
        string? contextSummary = null,
        CancellationToken cancellationToken = default)
        => gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: toolId,
                Arguments: arguments,
                Query: query,
                Context: context,
                ContextSummary: contextSummary),
            cancellationToken);
}
