using BenchmarkDotNet.Attributes;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewaySearchBenchmarks
{
    private BenchmarkGatewayHost _host = null!;

    [GlobalSetup]
    public async Task GlobalSetup() =>
        _host = await BenchmarkCatalog.CreateBuiltGraphGatewayAsync();

    [GlobalCleanup]
    public async Task GlobalCleanup() => await _host.DisposeAsync();

    [Benchmark]
    public async Task<int> SearchWeatherGraph()
    {
        var result = await _host.Gateway.SearchAsync(BenchmarkCatalog.WeatherQuery, maxResults: 5);
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public async Task<int> SearchPortfolioGraph()
    {
        var result = await _host.Gateway.SearchAsync(
            BenchmarkCatalog.PortfolioQuery,
            maxResults: 5
        );
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public async Task<int> SearchArchiveGraph()
    {
        var result = await _host.Gateway.SearchAsync(BenchmarkCatalog.ArchiveQuery, maxResults: 5);
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public async Task<int> SearchWeatherGraphTool()
    {
        var result = await _host.ToolSet.SchemaGraphSearchAsync(
            BenchmarkCatalog.WeatherQuery,
            maxResults: 5
        );
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }

    [Benchmark]
    public async Task<int> SearchArchiveGraphTool()
    {
        var result = await _host.ToolSet.SchemaGraphSearchAsync(
            BenchmarkCatalog.ArchiveQuery,
            maxResults: 5
        );
        return result.Matches.Count + result.RelatedMatches.Count + result.NextStepMatches.Count;
    }
}
