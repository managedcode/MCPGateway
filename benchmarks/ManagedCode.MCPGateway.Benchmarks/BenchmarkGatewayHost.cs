using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

internal sealed record BenchmarkGatewayHost(
    ServiceProvider ServiceProvider,
    IMcpGateway Gateway,
    McpGatewayToolSet ToolSet
) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ServiceProvider.DisposeAsync();
}
