using BenchmarkDotNet.Attributes;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayToolSetBenchmarks
{
    private BenchmarkGatewayHost _host = null!;
    private IReadOnlyList<McpGatewaySearchMatch> _matches = [];

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _host = await BenchmarkCatalog.CreateBuiltGraphGatewayAsync();
        var search = await _host.Gateway.SearchAsync(BenchmarkCatalog.WeatherQuery, maxResults: 10);
        _matches = search.Matches;
    }

    [GlobalCleanup]
    public async Task GlobalCleanup() => await _host.DisposeAsync();

    [Benchmark]
    public int CreateGatewayTools() => _host.ToolSet.CreateTools().Count;

    [Benchmark]
    public int CreateGraphTools() => _host.ToolSet.CreateGraphTools().Count;

    [Benchmark]
    public int CreateDiscoveredTools() => _host.ToolSet.CreateDiscoveredTools(_matches).Count;
}
