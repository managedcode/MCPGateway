using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayRefreshBenchmarks
{
    private ServiceProvider _provider = null!;
    private IMcpGatewayCatalogRuntime _catalog = null!;
    private IMcpGateway _gateway = null!;
    private int _revision;
    private const int ToolCount = 120;
    private const string InvalidRefreshMessage = "The refreshed graph did not expose the current source definition.";
    [GlobalSetup]
    public async Task SetupAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => Configure(options, 0));
        _provider = services.BuildServiceProvider();
        _gateway = _provider.GetRequiredService<IMcpGateway>();
        _catalog = _provider.GetRequiredService<IMcpGatewayCatalogRuntime>();
        await _gateway.BuildIndexAsync();
    }
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync();
    [Benchmark]
    public async Task<int> ReconfigureGraphAsync()
    {
        var revision = Interlocked.Increment(ref _revision);
        var options = new McpGatewayOptions();
        Configure(options, revision);
        await _catalog.ReconfigureAsync(options);
        var tools = await _gateway.ListToolsAsync();
        if (tools.Count != ToolCount || tools[0].Description != Description(revision))
        { throw new InvalidOperationException(InvalidRefreshMessage); }
        return tools.Count;
    }
    private static string Description(int revision) => $"Read workflow metadata at revision {revision}.";
    private static void Configure(McpGatewayOptions options, int revision)
    {
        for (var index = 0; index < ToolCount; index++)
        {
            options.AddTool("catalog", AIFunctionFactory.Create(static (string query) => query,
                new AIFunctionFactoryOptions { Name = $"record_read_{index:D5}", Description = Description(revision) }));
        }
    }
}
