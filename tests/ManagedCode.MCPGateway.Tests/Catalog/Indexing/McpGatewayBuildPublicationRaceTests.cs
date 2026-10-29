using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayBuildPublicationRaceTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatalogPollInterval = TimeSpan.FromMilliseconds(10);

    [Test]
    public async Task An_overtaken_build_reports_its_metadata_without_reading_its_retired_graph()
    {
        await using var source = await DynamicMcpCatalogHost.StartAsync();
        source.Description = $"Weather forecast {Guid.NewGuid():N}";
        var listingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ListingEntered = listingEntered;
        source.ListingRelease = releaseListing.Task;
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            options.AddMcpClient("original", source.Client));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var catalog = provider.GetRequiredService<IMcpGatewayCatalogRuntime>();
        var catalogSource = provider.GetRequiredService<IMcpGatewayCatalogSource>();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        guard.CancelAfter(CoordinationTimeout);
        var build = gateway.BuildIndexAsync(guard.Token);

        try
        {
            await listingEntered.Task.WaitAsync(guard.Token);
            var replacement = new McpGatewayOptions()
                .AddTool(TestFunctionFactory.CreateFunction((string query) => query,
                    "replacement_lookup", "Search the replacement catalog."))
                .AddTool(TestFunctionFactory.CreateFunction((string query) => query,
                    "replacement_history", "Read replacement history."));
            var reconfiguration = catalog.ReconfigureAsync(replacement, guard.Token).AsTask();
            while (!HasPublishedVersion(catalogSource, 1))
            {
                await Task.Delay(CatalogPollInterval, guard.Token);
            }

            await Assert.That(reconfiguration.IsCompleted).IsFalse();
            releaseListing.SetResult();
            var result = await build.WaitAsync(guard.Token);
            await reconfiguration.WaitAsync(guard.Token);

            await Assert.That(result.ToolCount).IsEqualTo(1);
            await Assert.That(result.IsGraphSearchEnabled).IsTrue();
            await Assert.That(result.GraphNodeCount).IsGreaterThan(0);
            await Assert.That(result.GraphEdgeCount).IsGreaterThan(0);
            var tools = await gateway.ListToolsAsync(guard.Token);
            await Assert.That(tools.Count).IsEqualTo(2);
            await Assert.That(tools.All(tool => tool.ToolName.StartsWith("replacement_", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            releaseListing.TrySetResult();
        }
    }

    private static bool HasPublishedVersion(IMcpGatewayCatalogSource source, int version)
    {
        using var snapshot = source.CreateSnapshot();
        return snapshot.Version >= version;
    }
}
