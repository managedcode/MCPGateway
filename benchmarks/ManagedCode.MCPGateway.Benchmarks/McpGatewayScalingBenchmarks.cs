using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayScalingBenchmarks
{
    [Params(1000, 5000, 10000)]
    public int ToolCount { get; set; }
    private ServiceProvider _provider = null!;
    private IMcpGateway _gateway = null!;
    private IMcpGatewayDiscovery _discovery = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetTotalAllocatedBytes();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            for (var index = 0; index < ToolCount; index++)
            {
                options.AddTool("directory", new CatalogFunction(index));
            }
        });
        _provider = services.BuildServiceProvider();
        _gateway = _provider.GetRequiredService<IMcpGateway>();
        _discovery = _provider.GetRequiredService<IMcpGatewayDiscovery>();
        await _gateway.BuildIndexAsync();
        Console.WriteLine($"Catalog initialization: tools={ToolCount}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}; allocated_bytes={GC.GetTotalAllocatedBytes() - allocated}; retained_managed_bytes={GC.GetTotalMemory(true)}");
        await CycleAsync();
    }
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync();

    [Benchmark]
    public async Task<int> CycleAsync()
    {
        var discovery = await _discovery.DiscoverAsync(new("directory_lookup_00000") { MaxBytes = 2048, MaxResults = 5 });
        var schema = await _discovery.GetToolSchemaAsync("directory_lookup_00000");
        var invoked = await _gateway.InvokeAsync(new(ToolId: schema!.Tool.ToolId,
            Arguments: new Dictionary<string, object?> { ["query"] = "benchmark" }));
        if (!invoked.IsSuccess || discovery.SerializedBytes > 2048)
        { throw new InvalidOperationException(InvalidCycleMessage); }
        return discovery.SerializedBytes;
    }
    private const string InvalidCycleMessage = "The measured discovery/schema/invocation cycle violated its contract.";

    private sealed class CatalogFunction(int index) : AIFunction
    {
        private const string QueryProperty = "query";
        public override string Name { get; } = $"directory_lookup_{index:D5}";
        public override string Description { get; } = $"Search contact directory workflow {index} and return its matching records.";
        public override JsonElement JsonSchema { get; } = CreateSchema(index);
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(arguments[QueryProperty]);
        private static JsonElement CreateSchema(int index)
        {
            var properties = new Dictionary<string, object>(StringComparer.Ordinal) { [QueryProperty] = new { type = "string" } };
            for (var field = 0; field < index % 20; field++)
            { properties.Add($"field_{field}", new { type = "string", maxLength = 512 }); }
            return JsonSerializer.SerializeToElement(new { type = "object", required = new[] { QueryProperty }, additionalProperties = false, properties });
        }
    }
}
