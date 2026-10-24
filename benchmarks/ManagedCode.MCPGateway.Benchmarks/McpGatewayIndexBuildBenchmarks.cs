using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

/// <summary>
/// Index builds for the 120-tool catalog and the 70-tool schema-heavy catalog. Cold cases use a new catalog revision
/// per operation, so no graph can be reused; the reuse cases build an identical catalog while another host holds it.
/// </summary>
[MemoryDiagnoser]
public class McpGatewayIndexBuildBenchmarks
{
    private const int ParallelHostCount = 10;
    private const string MissingReuseMessage = "The identical schema catalog did not reuse the live host's graph.";
    private ServiceProvider _liveSchemaHost = null!;
    private int _revision;

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _liveSchemaHost = BenchmarkSchemaCatalog.CreateServiceProvider();
        _ = await _liveSchemaHost.GetRequiredService<IMcpGateway>().BuildIndexAsync();
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync() => await _liveSchemaHost.DisposeAsync();

    [Benchmark]
    public async Task<int> BuildGraphIndex()
    {
        await using var serviceProvider = BenchmarkCatalog.CreateGraphServiceProvider(NextRevision());
        var result = await serviceProvider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        return result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount;
    }

    [Benchmark]
    public async Task<int> BuildSchemaCatalogGraphIndex()
    {
        await using var serviceProvider = BenchmarkSchemaCatalog.CreateServiceProvider(NextRevision());
        var result = await serviceProvider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        return result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount;
    }

    [Benchmark]
    public async Task<int> BuildSchemaCatalogGraphIndexReused()
    {
        await using var serviceProvider = BenchmarkSchemaCatalog.CreateServiceProvider();
        var result = await serviceProvider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        if (!result.IsGraphIndexReused)
        {
            throw new InvalidOperationException(MissingReuseMessage);
        }

        return result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount;
    }

    [Benchmark]
    public async Task<int> BuildSchemaCatalogGraphIndexParallelHosts()
    {
        var revision = NextRevision();
        var providers = new ServiceProvider[ParallelHostCount];
        for (var index = 0; index < providers.Length; index++)
        {
            providers[index] = BenchmarkSchemaCatalog.CreateServiceProvider(revision);
        }

        try
        {
            var results = await Task.WhenAll(providers.Select(static provider =>
                Task.Run(() => provider.GetRequiredService<IMcpGateway>().BuildIndexAsync())));
            return results.Sum(static result => result.ToolCount + result.GraphNodeCount + result.GraphEdgeCount);
        }
        finally
        {
            foreach (var provider in providers)
            {
                await provider.DisposeAsync();
            }
        }
    }

    private int NextRevision() => Interlocked.Increment(ref _revision);
}
