using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace ManagedCode.MCPGateway.Benchmarks;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["--profile"])
        {
            await McpGatewayProfilingWorkload.RunAsync();
            return;
        }

        if (args is ["--profile-index", var iterationText] && int.TryParse(iterationText, out var iterations))
        {
            await McpGatewayIndexProfilingWorkload.RunAsync(iterations);
            return;
        }

        if (args is ["--profile", var artifactDirectory])
        {
            await McpGatewayProfilingWorkload.RunAsync(artifactDirectory);
            return;
        }

        var config = ManualConfig.Create(DefaultConfig.Instance).AddColumn(StatisticColumn.P95).AddExporter(JsonExporter.Full);
        if (args is ["--ci-matrix"])
        {
            BenchmarkCi.WriteMatrix(config);
            return;
        }

        if (args is ["--ci-shard", var shardText, var artifacts] && int.TryParse(shardText, out var shard))
        {
            Environment.ExitCode = BenchmarkCi.RunShard(shard, artifacts, config);
            return;
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        if (summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)))
        {
            Environment.ExitCode = 1;
        }
    }
}
