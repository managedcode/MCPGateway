using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Benchmarks;

[MemoryDiagnoser]
public class McpGatewayGovernedScalingBenchmarks
{
    [Params(1000, 5000, 10000)]
    public int ToolCount { get; set; }
    private ServiceProvider _provider = null!;
    private IMcpGateway _gateway = null!;
    private IMcpGatewayDiscovery _discovery = null!;
    private readonly Actor _actor = new();

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var allocated = GC.GetTotalAllocatedBytes();
        var started = Stopwatch.GetTimestamp();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(_actor);
        services.AddSingleton<IMcpGatewayAccessPolicy, Policy>();
        services.AddMcpGateway(options =>
        {
            options.Execution.Enabled = true;
            for (var index = 1; index <= ToolCount; index++)
            {
                options.AddTool("catalog", new CatalogFunction(index), new McpGatewayToolSearchHints { EnabledByDefault = index % 7 != 0 });
            }
            options.AddMcpClientFactory("unavailable", static _ => ValueTask.FromException<McpClient>(new IOException(SourceUnavailableMessage)));
        });
        _provider = services.BuildServiceProvider();
        _gateway = _provider.GetRequiredService<IMcpGateway>();
        _discovery = _provider.GetRequiredService<IMcpGatewayDiscovery>();
        var built = await _gateway.BuildIndexAsync();
        Console.WriteLine($"Catalog initialization: tools={ToolCount}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}; allocated_bytes={GC.GetTotalAllocatedBytes() - allocated}; diagnostics={built.Diagnostics.Count}; retained_managed_bytes={GC.GetTotalMemory(true)}");
        await ConcurrentAuthorizedCyclesAsync();
    }
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync();

    [Benchmark]
    public async Task<int> ConcurrentAuthorizedCyclesAsync()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, CallerCount).Select(CycleAsync));
        return results.Sum();
    }
    private async Task<int> CycleAsync(int caller)
    {
        using var actor = _actor.Push(caller);
        var name = Name(caller);
        var discovered = await _discovery.DiscoverAsync(new(name) { MaxBytes = 2048 });
        var schema = await _discovery.GetToolSchemaAsync(name);
        var invoked = await _gateway.InvokeAsync(new(ToolId: name, Arguments: new Dictionary<string, JsonElement> { [QueryProperty] = JsonSerializer.SerializeToElement("benchmark") }));
        if (schema is null || !invoked.IsSuccess || discovered.SerializedBytes > 2048
            || discovered.Tools.Any(tool => tool.ToolId != name))
        { throw new InvalidOperationException(InvalidCycleMessage); }
        return discovered.SerializedBytes;
    }
    private const int CallerCount = 4;
    private const string QueryProperty = "query";
    private const string SourceUnavailableMessage = "The benchmark's configured offline source is unavailable.";
    private const string InvalidCycleMessage = "The authorized discovery/schema/invocation cycle violated its visibility or payload contract.";
    private static string Name(int index) => $"catalog_lookup_{index:D5}";
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        private readonly AsyncLocal<int> _caller = new();
        public IDisposable Push(int caller) { var previous = _caller.Value; _caller.Value = caller; return new Restore(this, previous); }
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext(_caller.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "tenant", "partition", "v1"));
        private sealed class Restore(Actor actor, int previous) : IDisposable { public void Dispose() => actor._caller.Value = previous; }
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(
                request.SourceId is null || request.AssetId == Name(int.Parse(context.SubjectId, System.Globalization.CultureInfo.InvariantCulture)))
            { IsReadOnly = true });
    }
    private sealed class CatalogFunction(int index) : AIFunction
    {
        public override string Name { get; } = McpGatewayGovernedScalingBenchmarks.Name(index);
        public override string Description { get; } = $"Find catalog workflow {index} and return its records.";
        public override JsonElement JsonSchema { get; } = CreateSchema(index);
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => ValueTask.FromResult(arguments[QueryProperty]);
        private static JsonElement CreateSchema(int index)
        {
            var properties = new Dictionary<string, object> { [QueryProperty] = new { type = "string" } };
            for (var field = 0; field < index % 20; field++)
            { properties.Add($"field_{field}", new { type = "string", maxLength = 512 }); }
            return JsonSerializer.SerializeToElement(new { type = "object", required = new[] { QueryProperty }, additionalProperties = false, properties });
        }
    }
}
