using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class SingleSourceTaskBindingResolver(
    IMcpGatewayFactory gatewayFactory,
    IMcpGatewayServerSource source,
    string toolName
) : IMcpGatewayServerBindingResolver
{
    public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken = default
    )
    {
        _ = requestServices;
        _ = serverServices;
        _ = server;
        cancellationToken.ThrowIfCancellationRequested();

        var gatewayInstance = gatewayFactory.Create(options =>
            options.AddTool(
                source.SourceId,
                TestFunctionFactory.CreateFunction(
                    static () => "not-used",
                    toolName,
                    "Represents an upstream task-capable tool."
                )
            )
        );

        return ValueTask.FromResult<IMcpGatewayServerBinding>(
            new McpGatewayServerBinding(
                gatewayInstance.Gateway,
                gatewayInstance.PromptCatalog,
                gatewayInstance.ResourceCatalog,
                gatewayInstance.Registry,
                listSourcesAsync: _ =>
                    ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>([source]),
                disposeAsync: gatewayInstance.DisposeAsync
            )
        );
    }
}
