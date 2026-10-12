using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayTrustedContext
{
    public static ValueTask<McpGatewayExecutionContext> ResolveAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetService<McpGatewayExecutionScope>()?.Current is { } frame ? ValueTask.FromResult(frame.Context)
        : services.GetService<IMcpGatewayExecutionContextProvider>() is { } provider
            ? provider.GetCurrentAsync(cancellationToken)
            : ValueTask.FromResult(McpGatewayExecutionContext.TrustedLocal);
    public static IServiceProvider OperationServices(IServiceProvider services) =>
        services.GetService<McpGatewayExecutionScope>()?.Current is { IsActive: true } frame ? frame.Services : services;
}
