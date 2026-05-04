using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayIndexBuildBenchmarks
{
    [Benchmark]
    public async Task<int> BuildGraphIndex()
    {
        var serviceProvider = BenchmarkCatalog.CreateGraphServiceProvider();
        try
        {
            var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
            var result = await gateway.BuildIndexAsync();
            return result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount;
        }
        finally
        {
            await serviceProvider.DisposeAsync();
        }
    }
}
