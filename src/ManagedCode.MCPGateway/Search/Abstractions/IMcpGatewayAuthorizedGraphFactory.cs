using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

/// <summary>Host projection for application-owned graphs. Returned graphs contain only permitted evidence and are disposed after the operation.</summary>
public interface IMcpGatewayAuthorizedGraphFactory
{
    ValueTask<KnowledgeGraph> CreateAsync(McpGatewayExecutionContext context, KnowledgeGraph source,
        IReadOnlyList<McpGatewayToolDescriptor> visibleTools, CancellationToken cancellationToken = default);
}
