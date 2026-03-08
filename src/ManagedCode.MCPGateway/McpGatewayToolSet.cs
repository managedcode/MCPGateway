using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayToolSet(IMcpGateway gateway)
{
    public const string DefaultSearchToolName = "gateway_tools_search";
    public const string DefaultInvokeToolName = "gateway_tool_invoke";
    public const string DiscoveredToolIdPropertyName = "ManagedCode.MCPGateway.ToolId";
    public const string DiscoveredToolSourceIdPropertyName = "ManagedCode.MCPGateway.SourceId";
    public const string DiscoveredToolKindPropertyName = "ManagedCode.MCPGateway.Kind";
    public const string SearchToolDescription = "Search the gateway catalog and return the best matching tools for a user task.";
    public const string InvokeToolDescription = "Invoke a gateway tool by tool id. Search first when the correct tool is unknown.";
    private const string DiscoveredToolKindValue = "gateway_discovered_tool";
    private const string DiscoveredToolNameSeparator = "_";
    private const string DiscoveredToolDescriptionPrefix = "Direct proxy for gateway tool ";
    private const string DiscoveredToolIdLabel = " (";
    private const string DiscoveredToolDescriptionSeparator = "). ";
    private const string DiscoveredToolRequiredArgumentsLabel = "Required arguments: ";
    private const string DiscoveredToolArgumentsHint = "Pass named inputs via 'arguments' and use 'query' for free-text tool inputs when supported.";

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

    public IList<AITool> AddTools(
        IList<AITool> tools,
        string searchToolName = DefaultSearchToolName,
        string invokeToolName = DefaultInvokeToolName)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var targetTools = new List<AITool>(tools);
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in targetTools)
        {
            toolNames.Add(tool.Name);
        }

        foreach (var tool in CreateTools(searchToolName, invokeToolName))
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
        int? maxTools = null)
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

            var functionName = CreateDiscoveredToolName(match, reservedNames);
            discoveredTools.Add(CreateDiscoveredTool(match, functionName));
        }

        return discoveredTools;
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

    private AITool CreateDiscoveredTool(
        McpGatewaySearchMatch match,
        string functionName)
    {
        Task<McpGatewayInvokeResult> InvokeDiscoveredToolAsync(
            Dictionary<string, object?>? arguments = null,
            string? query = null,
            Dictionary<string, object?>? context = null,
            string? contextSummary = null,
            CancellationToken cancellationToken = default)
            => gateway.InvokeAsync(
                new McpGatewayInvokeRequest(
                    ToolId: match.ToolId,
                    Arguments: arguments,
                    Query: query,
                    Context: context,
                    ContextSummary: contextSummary),
                cancellationToken);

        return AIFunctionFactory.Create(
            (Func<Dictionary<string, object?>?, string?, Dictionary<string, object?>?, string?, CancellationToken, Task<McpGatewayInvokeResult>>)InvokeDiscoveredToolAsync,
            new AIFunctionFactoryOptions
            {
                Name = functionName,
                Description = BuildDiscoveredToolDescription(match),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [DiscoveredToolIdPropertyName] = match.ToolId,
                    [DiscoveredToolSourceIdPropertyName] = match.SourceId,
                    [DiscoveredToolKindPropertyName] = DiscoveredToolKindValue
                }
            });
    }

    private static string BuildDiscoveredToolDescription(McpGatewaySearchMatch match)
    {
        var description = $"{DiscoveredToolDescriptionPrefix}{match.ToolName}{DiscoveredToolIdLabel}{match.ToolId}{DiscoveredToolDescriptionSeparator}{match.Description}";
        if (match.RequiredArguments.Count == 0)
        {
            return $"{description} {DiscoveredToolArgumentsHint}";
        }

        return $"{description} {DiscoveredToolRequiredArgumentsLabel}{BuildRequiredArgumentList(match.RequiredArguments)}. {DiscoveredToolArgumentsHint}";
    }

    private static string BuildRequiredArgumentList(IReadOnlyList<string> requiredArguments)
    {
        if (requiredArguments.Count == 1)
        {
            return requiredArguments[0];
        }

        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < requiredArguments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(requiredArguments[index]);
        }

        return builder.ToString();
    }

    private static string CreateDiscoveredToolName(
        McpGatewaySearchMatch match,
        ISet<string> reservedNames)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(reservedNames);

        var sanitizedToolName = SanitizeToolName(match.ToolName);
        if (reservedNames.Add(sanitizedToolName))
        {
            return sanitizedToolName;
        }

        var sanitizedSourceId = SanitizeToolName(match.SourceId);
        var compositeName = $"{sanitizedSourceId}{DiscoveredToolNameSeparator}{sanitizedToolName}";
        if (reservedNames.Add(compositeName))
        {
            return compositeName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var uniqueName = $"{compositeName}{DiscoveredToolNameSeparator}{suffix}";
            if (reservedNames.Add(uniqueName))
            {
                return uniqueName;
            }
        }
    }

    private static string SanitizeToolName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "gateway_tool";
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (builder.Length == 0)
        {
            return "gateway_tool";
        }

        if (!char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, "t_");
        }

        return builder.ToString();
    }
}
