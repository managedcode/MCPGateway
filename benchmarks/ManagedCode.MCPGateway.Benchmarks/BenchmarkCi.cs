using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace ManagedCode.MCPGateway.Benchmarks;

internal static class BenchmarkCi
{
    private const BindingFlags BenchmarkMethods =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void WriteMatrix(IConfig config)
    {
        var shards = DiscoverShards(config);
        if (shards.Length == 0)
        {
            throw new InvalidOperationException("Benchmark discovery returned no cases.");
        }

        var include = shards.Select((cases, shard) => new
        {
            shard,
            label = cases.Length == 1
                ? $"{cases[0].Descriptor.Type.Name} {cases[0].Parameters.DisplayInfo}".TrimEnd()
                : cases[0].Descriptor.Type.Name,
            cases = cases.Length,
        });
        Console.WriteLine(JsonSerializer.Serialize(new { include }));
    }

    public static int RunShard(int shard, string artifacts, ManualConfig config)
    {
        var shards = DiscoverShards(config);
        if ((uint)shard >= (uint)shards.Length)
        {
            Console.Error.WriteLine($"Unknown benchmark shard {shard}.");
            return 1;
        }

        var expected = shards[shard].Select(benchmark => benchmark.DisplayInfo).ToHashSet(StringComparer.Ordinal);
        config.AddFilter(new SimpleFilter(benchmark => expected.Contains(benchmark.DisplayInfo)));
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(["--filter", "*", "--artifacts", artifacts], config).ToArray();
        return ValidateResults(expected, summaries);
    }

    private static BenchmarkCase[][] DiscoverShards(IConfig config) =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.GetMethods(BenchmarkMethods)
                .Any(method => method.IsDefined(typeof(BenchmarkAttribute))))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type => SplitCases(BenchmarkConverter.TypeToBenchmarks(type, config).BenchmarksCases))
            .ToArray();

    private static IEnumerable<BenchmarkCase[]> SplitCases(BenchmarkCase[] cases)
    {
        var ordinary = cases.Where(benchmark => !benchmark.HasParameters).ToArray();
        if (ordinary.Length > 0)
        {
            yield return ordinary;
        }

        foreach (var benchmark in cases.Where(benchmark => benchmark.HasParameters)
            .OrderBy(benchmark => benchmark.DisplayInfo, StringComparer.Ordinal))
        {
            yield return [benchmark];
        }
    }

    private static int ValidateResults(HashSet<string> expected, Summary[] summaries)
    {
        var reports = summaries.SelectMany(summary => summary.Reports).ToArray();
        if (summaries.Any(summary => summary.HasCriticalValidationErrors)
            || reports.Length != expected.Count
            || !expected.SetEquals(reports.Select(report => report.BenchmarkCase.DisplayInfo)))
        {
            Console.Error.WriteLine($"Expected {expected.Count} benchmark cases in this shard, received {reports.Length}. Check the case identities and validation output.");
            return 1;
        }

        foreach (var report in reports)
        {
            if (!report.Success || report.ResultStatistics is not { } statistics || !double.IsFinite(statistics.Mean)
                || !report.Metrics.Values.Any(metric => metric.Descriptor.DisplayName == Column.Allocated && double.IsFinite(metric.Value)))
            {
                Console.Error.WriteLine($"Missing successful timing or allocation measurements for {report.BenchmarkCase.DisplayInfo}.");
                return 1;
            }
        }

        Console.WriteLine($"Verified all {reports.Length} benchmark cases in this shard.");
        return 0;
    }
}
