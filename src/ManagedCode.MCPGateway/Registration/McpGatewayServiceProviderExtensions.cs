using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

public static class McpGatewayServiceProviderExtensions
{
    public static Task<McpGatewayIndexBuildResult> InitializeManagedCodeMcpGatewayAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider.GetRequiredService<IMcpGateway>().BuildIndexAsync(cancellationToken);
    }
}
