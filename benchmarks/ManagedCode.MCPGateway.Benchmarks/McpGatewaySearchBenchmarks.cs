using BenchmarkDotNet.Attributes;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewaySearchBenchmarks
{
    private BenchmarkGatewayHost _host = null!;

    [GlobalSetup]
    public void GlobalSetup() =>
        _host = BenchmarkCatalog.CreateBuiltGraphGatewayAsync().GetAwaiter().GetResult();

    [GlobalCleanup]
    public void GlobalCleanup() => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public int SearchWeatherGraph()
    {
        var result = _host
            .Gateway.SearchAsync(BenchmarkCatalog.WeatherQuery, maxResults: 5)
            .GetAwaiter()
            .GetResult();
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public int SearchPortfolioGraph()
    {
        var result = _host
            .Gateway.SearchAsync(BenchmarkCatalog.PortfolioQuery, maxResults: 5)
            .GetAwaiter()
            .GetResult();
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public int SearchArchiveGraph()
    {
        var result = _host
            .Gateway.SearchAsync(BenchmarkCatalog.ArchiveQuery, maxResults: 5)
            .GetAwaiter()
            .GetResult();
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public int SearchWeatherGraphTool()
    {
        var result = _host
            .ToolSet.SchemaGraphSearchAsync(BenchmarkCatalog.WeatherQuery, maxResults: 5)
            .GetAwaiter()
            .GetResult();
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }
}
