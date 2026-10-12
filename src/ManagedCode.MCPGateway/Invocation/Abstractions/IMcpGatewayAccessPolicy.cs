namespace ManagedCode.MCPGateway;

public enum McpGatewayOperation
{
    Discover,
    InspectSchema,
    Invoke,
    RenderPrompt,
    ReadResource,
    CompleteArgument,
    SubscribeResource,
    ReceiveResourceUpdate,
    SubscribePromptChanges,
    ReceivePromptChanges,
    ReadGraph,
    ExportGraph,
    AdministerGraph,
    FederateGraph,
    AccessTask,
    ReadResult,
    DeliverResult,
    ManageCatalog,
}

public sealed record McpGatewayAccessRequest(McpGatewayOperation Operation, string AssetId, string? SourceId = null)
{
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];
    public string? DefinitionHash { get; init; }
}

public sealed record McpGatewayAccessDecision(bool IsAllowed)
{
    /// <summary>Host-approved execution semantics; upstream hints do not grant retry permission.</summary>
    public bool IsReadOnly { get; init; }
    public static McpGatewayAccessDecision Allowed { get; } = new(true);
    public static McpGatewayAccessDecision Denied { get; } = new(false);
}

/// <summary>One reusable policy for every caller-visible gateway capability.</summary>
public interface IMcpGatewayAccessPolicy
{
    ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
        CancellationToken cancellationToken = default);

    async ValueTask<IReadOnlySet<string>> FilterDiscoveryAsync(McpGatewayExecutionContext context,
        IReadOnlyList<McpGatewayToolDescriptor> tools, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if ((await AuthorizeAsync(context, new(McpGatewayOperation.Discover, tool.ToolId, tool.SourceId)
            { RequiredPermissions = tool.RequiredPermissions, DefinitionHash = tool.DefinitionHash }, cancellationToken)).IsAllowed)
            {
                allowed.Add(tool.ToolId);
            }
        }
        return allowed;
    }
}
