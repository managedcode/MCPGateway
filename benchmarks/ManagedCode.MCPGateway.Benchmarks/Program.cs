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

        if (args is ["--profile", var artifactDirectory])
        {
            await McpGatewayProfilingWorkload.RunAsync(artifactDirectory);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
