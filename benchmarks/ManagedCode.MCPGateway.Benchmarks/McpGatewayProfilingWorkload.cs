using System.Diagnostics;
using System.Text.Json;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Benchmarks;

internal static class McpGatewayProfilingWorkload
{
    private const int MaximumPhaseSeconds = 300;
    private const int MaximumResults = 5;

    public static async Task RunAsync(string? artifactDirectory = null)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        await using var host = await BenchmarkCatalog.CreateBuiltGraphGatewayAsync();
        if (artifactDirectory is not null)
        {
            await ExportAsync(host, artifactDirectory, cancellation.Token);
        }
        Write(new
        {
            ready = true,
            pid = Environment.ProcessId,
            runtime = Environment.Version.ToString(),
            gatewayVersion = typeof(McpGateway).Assembly.GetName().Version?.ToString(),
            graphVersion = typeof(KnowledgeGraph).Assembly.GetName().Version?.ToString(),
            tools = (await host.Gateway.ListToolsAsync(cancellation.Token)).Count,
        });

        try
        {
            while (!cancellation.IsCancellationRequested
                && await Console.In.ReadLineAsync(cancellation.Token) is { } command)
            {
                if (command == "exit")
                {
                    return;
                }

                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[1], out var seconds)
                    || seconds < 1 || seconds > MaximumPhaseSeconds)
                {
                    throw new ArgumentException("Use weather|archive|exact|no-match|weather-tool|archive-tool|no-match-tool <seconds 1..300>, or exit.");
                }

                await RunPhaseAsync(host, parts[0], seconds, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Profiling workload cancelled.");
        }
    }

    private static async Task ExportAsync(BenchmarkGatewayHost host, string artifactDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(artifactDirectory);
        Directory.CreateDirectory(root);
        var export = await ((IMcpGatewayGraphSearch)host.Gateway).ExportMarkdownLdGraphAsync(cancellationToken);
        var search = await host.ToolSet.SchemaGraphSearchAsync(BenchmarkCatalog.WeatherQuery, MaximumResults, cancellationToken);
        var sparql = search.GeneratedSparql ?? throw new InvalidOperationException("Schema search did not generate SPARQL.");
        await File.WriteAllTextAsync(Path.Combine(root, "gateway-catalog.jsonld"), export.JsonLd, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "gateway-weather.sparql"), sparql, cancellationToken);
    }

    private static async Task RunPhaseAsync(
        BenchmarkGatewayHost host,
        string phase,
        int seconds,
        CancellationToken cancellationToken
    )
    {
        var expected = await SearchAsync(host, phase, cancellationToken);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Validate(await SearchAsync(host, phase, cancellationToken), expected);
        }

        Write(new { started = phase, pid = Environment.ProcessId, seconds, expected });
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        long operations = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(await SearchAsync(host, phase, cancellationToken), expected);
            operations++;
        }

        var elapsed = clock.Elapsed.TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Write(new
        {
            phase,
            pid = Environment.ProcessId,
            operations,
            elapsedMilliseconds = elapsed,
            allocatedBytes = allocated,
            millisecondsPerOperation = elapsed / operations,
            allocatedBytesPerOperation = (double)allocated / operations,
        });
    }

    private static async Task<ProfileResult> SearchAsync(
        BenchmarkGatewayHost host,
        string phase,
        CancellationToken cancellationToken
    )
    {
        if (phase is "weather-tool" or "archive-tool" or "no-match-tool")
        {
            var query = phase switch
            {
                "weather-tool" => BenchmarkCatalog.WeatherQuery,
                "archive-tool" => BenchmarkCatalog.ArchiveQuery,
                _ => BenchmarkCatalog.MissingQuery,
            };
            var result = await host.ToolSet.SchemaGraphSearchAsync(query, MaximumResults, cancellationToken);
            return new(result.Matches.Count, result.RelatedMatches.Count, result.NextStepMatches.Count, true, false);
        }

        var searchQuery = phase switch
        {
            "weather" => BenchmarkCatalog.WeatherQuery,
            "archive" => BenchmarkCatalog.ArchiveQuery,
            "exact" => BenchmarkCatalog.ExactQuery,
            "no-match" => BenchmarkCatalog.MissingQuery,
            _ => throw new ArgumentException("Unknown profiling phase.", nameof(phase)),
        };
        var search = await host.Gateway.SearchAsync(searchQuery, MaximumResults, cancellationToken);
        return new(search.Matches.Count, search.RelatedMatches.Count, search.NextStepMatches.Count,
            search.UsedSchemaSearch, search.UsedSchemaFallback);
    }

    private static void Validate(ProfileResult actual, ProfileResult expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Profiling search behavior changed: expected {expected}, got {actual}.");
        }
    }

    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private sealed record ProfileResult(int Primary, int Related, int NextStep, bool Schema, bool Fallback);
}
