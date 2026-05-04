using BenchmarkDotNet.Attributes;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayToolSetBenchmarks
{
    private BenchmarkGatewayHost _host = null!;
    private IReadOnlyList<McpGatewaySearchMatch> _matches = [];

    [GlobalSetup]
    public void GlobalSetup()
    {
        _host = BenchmarkCatalog.CreateBuiltGraphGatewayAsync().GetAwaiter().GetResult();
        var search = _host
            .Gateway.SearchAsync(BenchmarkCatalog.WeatherQuery, maxResults: 10)
            .GetAwaiter()
            .GetResult();
        _matches = search.Matches;
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public int CreateGatewayTools() => _host.ToolSet.CreateTools().Count;

    [Benchmark]
    public int CreateGraphTools() => _host.ToolSet.CreateGraphTools().Count;

    [Benchmark]
    public int CreateDiscoveredTools() => _host.ToolSet.CreateDiscoveredTools(_matches).Count;
}
