using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayIndexBuildBenchmarks
{
    [Benchmark]
    public int BuildGraphIndex()
    {
        var serviceProvider = BenchmarkCatalog.CreateGraphServiceProvider();
        try
        {
            var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
            var result = gateway.BuildIndexAsync().GetAwaiter().GetResult();
            return result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount;
        }
        finally
        {
            serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
