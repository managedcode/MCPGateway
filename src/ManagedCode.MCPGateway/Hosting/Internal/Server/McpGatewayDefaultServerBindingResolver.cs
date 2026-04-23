using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayDefaultServerBindingResolver(
    McpGatewayPromptChangeHub promptChangeHub
) : IMcpGatewayServerBindingResolver
{
    public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(serverServices);
        ArgumentNullException.ThrowIfNull(server);
        cancellationToken.ThrowIfCancellationRequested();

        var services = requestServices ?? serverServices;

        return ValueTask.FromResult<IMcpGatewayServerBinding>(
            new McpGatewayServerBinding(
                services.GetRequiredService<IMcpGateway>(),
                services.GetRequiredService<IMcpGatewayPromptCatalog>(),
                services.GetRequiredService<IMcpGatewayResourceCatalog>(),
                services.GetRequiredService<IMcpGatewayRegistry>(),
                subscribeToPromptListChanges: promptChangeHub.Subscribe
            )
        );
    }
}
