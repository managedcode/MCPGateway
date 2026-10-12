using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

/// <summary>A bounded, staged discovery profile without graph administration tools.</summary>
public sealed class McpGatewayDiscoveryToolSet(IMcpGateway gateway, IMcpGatewayDiscovery discovery, IServiceProvider? services = null)
{
    public const string SchemaToolName = "gateway_tool_schema";
    public const string SummaryToolName = "gateway_tools_discover";

    public const string ResultToolName = "gateway_result_read";

    public IReadOnlyList<AITool> CreateTools()
    {
        List<AITool> tools =
    [
        AIFunctionFactory.Create(DiscoverAsync, new AIFunctionFactoryOptions { Name = SummaryToolName, Description = "Discover compact tool summaries. Follow continuation when present, inspect the chosen schema, then invoke." }),
        AIFunctionFactory.Create(GetSchemaAsync, new AIFunctionFactoryOptions { Name = SchemaToolName, Description = "Inspect the current complete input/output schema and revision of one gateway tool." }),
        new McpGatewayToolSet(gateway).CreateTools().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName),
    ];
        if (services?.GetService<IMcpGatewayResultReader>() is not null)
        {
            tools.Add(AIFunctionFactory.Create(ReadResultAsync, new AIFunctionFactoryOptions
            { Name = ResultToolName, Description = "Read an approved stored result using an opaque handle, optional JSON Pointer projection and a byte budget." }));
        }
        return tools;
    }

    private Task<McpGatewayStoredResult?> ReadResultAsync(string handle, McpGatewayResponseOptions? response = null,
        CancellationToken cancellationToken = default) => services!.GetRequiredService<IMcpGatewayResultReader>().ReadAsync(handle, response, cancellationToken);

    public async Task<IReadOnlyList<AITool>> CreateSchemaFaithfulToolsAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var functions = new List<AITool>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolId in toolIds)
        {
            if (!ids.Add(toolId))
            {
                continue;
            }
            var schema = await discovery.GetToolSchemaAsync(toolId, cancellationToken)
                ?? throw new InvalidOperationException("The tool is unavailable for schema inspection.");
            if (schema.Tool.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("The tool has no available input schema.");
            }
            functions.Add(new McpGatewaySchemaFaithfulFunction(gateway, schema.Tool));
        }
        return functions;
    }

    private Task<McpGatewayDiscoveryResult> DiscoverAsync(string? query, int? maxResults = null, int maxBytes = McpGatewayDiscoveryRequest.DefaultMaxBytes,
        string? sourceId = null, string? category = null, string? continuationToken = null, CancellationToken cancellationToken = default) =>
        discovery.DiscoverAsync(new(query) { MaxResults = maxResults, MaxBytes = maxBytes, SourceId = sourceId, Category = category, ContinuationToken = continuationToken }, cancellationToken);

    private async Task<object?> GetSchemaAsync(string toolId, CancellationToken cancellationToken = default)
    {
        try
        { return await discovery.GetToolSchemaAsync(toolId, cancellationToken); }
        catch (McpGatewaySchemaTooLargeException exception)
        {
            return new
            {
                IsSuccess = false,
                McpGatewaySchemaTooLargeException.ErrorCode,
                exception.ActualBytes,
                exception.MaximumBytes,
                exception.SchemaHash,
                exception.Revision
            };
        }
    }
}
