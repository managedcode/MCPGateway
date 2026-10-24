using System.Diagnostics;
using System.Text.Json;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

/// <summary>
/// Attributes index-build time and managed allocation for the schema-heavy catalog through public gateway and
/// Markdown-LD APIs: complete gateway builds (cold, repeated and parallel hosts) and the Markdown-LD stages the
/// gateway runs for a generated tool graph. Every cold build uses a new catalog revision.
/// </summary>
internal static class McpGatewayIndexProfilingWorkload
{
    private const int WarmupIterations = 2;
    private const int MinimumIterations = 1;
    private const int MaximumIterations = 50;
    private const int ParallelHosts = 10;
    private const string GraphBaseUri = "https://managedcode.com/mcpgateway/knowledge/";
    private const int GraphMaxRelatedTokenSegments = 6;
    private static int _revision;

    public static async Task RunAsync(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinimumIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaximumIterations);
        var descriptors = await ReadDescriptorsAsync();
        var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors, BenchmarkSchemaCatalog.MaxDescriptorLength);
        var sources = documents
            .Select(static document => new MarkdownSourceDocument(document.Path, document.Content, new Uri(document.CanonicalUri!)))
            .ToArray();
        Write(new
        {
            catalog = "schema",
            tools = descriptors.Count,
            documentCharacters = documents.Sum(static document => document.Content.Length),
            runtime = Environment.Version.ToString(),
            gatewayVersion = typeof(McpGateway).Assembly.GetName().Version?.ToString(),
            graphVersion = typeof(KnowledgeGraph).Assembly.GetName().Version?.ToString(),
            iterations,
        });

        await MeasureRetainedAsync();
        await MeasureAsync("gateway_cold_build", iterations, () => BuildAsync(NextRevision()));
        await MeasureAsync("gateway_repeated_build_identical_catalog", iterations, () => BuildAsync(revision: 0));
        await MeasureAsync("gateway_build_while_identical_host_alive", iterations, BuildWhileIdenticalHostAliveAsync);
        await MeasureAsync("gateway_10_parallel_hosts_cold_identical_catalog", iterations, BuildParallelHostsAsync);
        await MeasureAsync("generate_tool_markdown", iterations, () =>
            Task.FromResult(McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors, BenchmarkSchemaCatalog.MaxDescriptorLength).Count));
        await MeasureAsync("markdown_ld_build_rules_only", iterations, () => BuildGraphAsync(sources, MarkdownKnowledgeExtractionMode.None));
        await MeasureAsync("markdown_ld_build_tiktoken", iterations, () => BuildGraphAsync(sources, MarkdownKnowledgeExtractionMode.Tiktoken));
        await MeasureAsync("graph_full_text_index", iterations, () => BuildFullTextAsync(sources));
    }

    private static int NextRevision() => Interlocked.Increment(ref _revision);

    private static async Task MeasureRetainedAsync()
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        await using var provider = BenchmarkSchemaCatalog.CreateServiceProvider(NextRevision());
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var result = await gateway.BuildIndexAsync();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Write(new { stage = "retained_first_built_gateway", retainedBytes = after - before, result.GraphNodeCount, result.GraphEdgeCount });
        GC.KeepAlive(gateway);
    }

    private static async Task<IReadOnlyList<McpGatewayToolDescriptor>> ReadDescriptorsAsync()
    {
        await using var provider = BenchmarkSchemaCatalog.CreateServiceProvider();
        return await provider.GetRequiredService<IMcpGateway>().ListToolsAsync();
    }

    private static async Task<int> BuildAsync(int revision)
    {
        await using var provider = BenchmarkSchemaCatalog.CreateServiceProvider(revision);
        var result = await provider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        return result.GraphNodeCount;
    }

    private static async Task<int> BuildWhileIdenticalHostAliveAsync()
    {
        var revision = NextRevision();
        await using var first = BenchmarkSchemaCatalog.CreateServiceProvider(revision);
        await using var second = BenchmarkSchemaCatalog.CreateServiceProvider(revision);
        _ = await first.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = await second.GetRequiredService<IMcpGateway>().BuildIndexAsync();
        Write(new
        {
            stage = "second_host_build_only",
            milliseconds = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
        });
        return result.GraphNodeCount;
    }

    private static async Task<int> BuildParallelHostsAsync()
    {
        var revision = NextRevision();
        var providers = new ServiceProvider[ParallelHosts];
        for (var index = 0; index < providers.Length; index++)
        {
            providers[index] = BenchmarkSchemaCatalog.CreateServiceProvider(revision);
        }

        try
        {
            var builds = providers.Select(static provider =>
                Task.Run(() => provider.GetRequiredService<IMcpGateway>().BuildIndexAsync()));
            var results = await Task.WhenAll(builds);
            return results.Sum(static result => result.GraphNodeCount);
        }
        finally
        {
            foreach (var provider in providers)
            {
                await provider.DisposeAsync();
            }
        }
    }

    private static async Task<int> BuildGraphAsync(IReadOnlyList<MarkdownSourceDocument> sources, MarkdownKnowledgeExtractionMode mode)
    {
        var result = await CreatePipeline(mode).BuildAsync(sources);
        using var graph = result.Graph;
        return graph.TripleCount;
    }

    private static async Task<int> BuildFullTextAsync(IReadOnlyList<MarkdownSourceDocument> sources)
    {
        var result = await CreatePipeline(MarkdownKnowledgeExtractionMode.Tiktoken).BuildAsync(sources);
        using var graph = result.Graph;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        using var index = await graph.BuildFullTextIndexAsync();
        Write(new
        {
            stage = "full_text_index_only",
            milliseconds = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
        });
        return graph.TripleCount;
    }

    private static MarkdownKnowledgePipeline CreatePipeline(MarkdownKnowledgeExtractionMode mode) =>
        new(
            new Uri(GraphBaseUri, UriKind.Absolute),
            extractionMode: mode,
            tiktokenOptions: new TiktokenKnowledgeGraphOptions { MaxRelatedSegments = GraphMaxRelatedTokenSegments }
        );

    private static async Task MeasureAsync(string stage, int iterations, Func<Task<int>> operation)
    {
        var expected = 0;
        for (var warmup = 0; warmup < WarmupIterations; warmup++)
        {
            expected = await operation();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (await operation() != expected)
            {
                throw new InvalidOperationException($"Stage '{stage}' produced a different result.");
            }
        }

        Write(new
        {
            stage,
            millisecondsPerOperation = clock.Elapsed.TotalMilliseconds / iterations,
            allocatedBytesPerOperation = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / iterations,
            result = expected,
        });
    }

    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
