using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySourceRefreshIntegrationTests
{
    [Test]
    public async Task An_initially_empty_SDK_source_can_announce_its_first_tool()
    {
        await using var source = await DynamicMcpCatalogHost.StartAsync();
        source.EmptyTools = true;
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddMcpClient("weather", source.Client));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await Assert.That((await gateway.ListToolsAsync()).Count).IsEqualTo(0);
        source.EmptyTools = false;
        await source.NotifyAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await gateway.ListToolsAsync(guard.Token)).Count == 0)
        { await Task.Delay(10, guard.Token); }
        await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue();
    }
    [Test]
    public async Task Hosted_refresh_updates_only_explicit_sources_and_stops_cleanly()
    {
        await using var source = await DynamicMcpCatalogHost.StartAsync();
        await using var other = await DynamicMcpCatalogHost.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => { options.AddMcpClient("weather", source.Client); options.AddMcpClient("archive", other.Client); });
        services.AddMcpGatewaySourceRefresh(options =>
        {
            options.SourceIds.Add("weather");
            options.Interval = TimeSpan.FromMilliseconds(20);
            options.InitialBackoff = TimeSpan.FromMilliseconds(20);
            options.MaximumBackoff = TimeSpan.FromMilliseconds(100);
        });
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var otherCalls = other.ListCalls;
        var worker = provider.GetServices<IHostedService>().Single();
        source.Description = "Scheduled precipitation forecast";
        await worker.StartAsync(CancellationToken.None);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while ((await gateway.ListToolsAsync(guard.Token)).Single(item => item.SourceId == "weather").Description != source.Description)
            { await Task.Delay(10, guard.Token); }
            await Assert.That(other.ListCalls).IsEqualTo(otherCalls);
        }
        finally { await worker.StopAsync(guard.Token); }
    }
    [Test]
    public async Task Source_refresh_budget_preserves_old_catalog_and_a_cancelled_waiter_does_not_cancel_shared_refresh()
    {
        await using var source = await DynamicMcpCatalogHost.StartAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        { options.SourceRefreshTimeout = TimeSpan.FromMilliseconds(100); options.AddMcpClient("weather", source.Client); });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        source.ListingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ListingRelease = release.Task;
        var runtime = provider.GetRequiredService<IMcpGatewayCatalogRuntime>();
        var shared = runtime.RefreshSourceAsync("weather").AsTask();
        await source.ListingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var waiter = runtime.RefreshSourceAsync("weather", cancelled.Token).AsTask();
        await cancelled.CancelAsync();
        try
        { await waiter; Assert.Fail("The cancelled caller must stop waiting."); }
        catch (OperationCanceledException) { await Assert.That(shared.IsCompleted).IsFalse(); }
        try
        { await shared; Assert.Fail("The source refresh must respect its independent budget."); }
        catch (OperationCanceledException) { await Assert.That((await gateway.ListToolsAsync()).Single().Description).IsEqualTo("Weather forecast"); }
        finally { release.TrySetResult(); source.ListingRelease = null; }
        await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue();
    }
    [Test]
    public async Task Hosted_failed_refresh_uses_backoff_preserves_snapshot_and_recovers_the_same_source()
    {
        await using var source = await DynamicMcpCatalogHost.StartAsync();
        var clock = new RefreshClock();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMcpGateway(options => options.AddMcpClient("weather", source.Client));
        services.AddMcpGatewaySourceRefresh(options =>
        {
            options.SourceIds.Add("weather");
            options.Interval = TimeSpan.FromMilliseconds(500);
            options.InitialBackoff = TimeSpan.FromMilliseconds(10);
            options.MaximumBackoff = TimeSpan.FromMilliseconds(50);
        });
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        source.RejectListing = true;
        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref source.ListCalls) < 3)
            { await Task.Delay(10, guard.Token); }
            await Assert.That((await gateway.ListToolsAsync()).Single().Description).IsEqualTo("Weather forecast");
            await Assert.That(clock.Delays.Any(delay => delay > TimeSpan.Zero && delay < TimeSpan.FromMilliseconds(100))).IsTrue();
            source.Description = "Recovered weather";
            source.RejectListing = false;
            while ((await gateway.ListToolsAsync(guard.Token)).Single().Description != source.Description)
            { await Task.Delay(10, guard.Token); }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class RefreshClock : TimeProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Delays { get; } = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Delays.Enqueue(dueTime); return base.CreateTimer(callback, state, dueTime, period); }
    }

    [TUnit.Core.Test]
    public async Task Source_refresh_is_single_flight_reuses_other_connections_and_preserves_runtime_on_failure()
    {
        await using var first = await DynamicMcpCatalogHost.StartAsync();
        await using var second = await DynamicMcpCatalogHost.StartAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        { options.AddMcpClient("weather", first.Client); options.AddMcpClient("archive", second.Client); });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var otherCalls = second.ListCalls;
        first.Description = "Revised precipitation forecast";
        first.ListingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ListingRelease = release.Task;
        var runtime = provider.GetRequiredService<IMcpGatewayCatalogRuntime>();
        var refresh = runtime.RefreshSourceAsync("weather").AsTask();
        await first.ListingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var same = runtime.RefreshSourceAsync("weather").AsTask();
        release.SetResult();
        await Task.WhenAll(refresh, same);
        await Assert.That(second.ListCalls).IsEqualTo(otherCalls);
        await Assert.That((await gateway.ListToolsAsync()).Single(tool => tool.SourceId == "weather").Description).IsEqualTo(first.Description);
        first.RejectListing = true;
        try
        { await runtime.RefreshSourceAsync("weather"); Assert.Fail("An invalid refresh must fail."); }
        catch (InvalidOperationException exception) { await Assert.That(exception.Message).DoesNotContain("SEEDED_PRIVATE_LIST_FAILURE"); }
        await Assert.That((await gateway.InvokeAsync(new(SourceId: "weather", ToolName: "lookup"))).IsSuccess).IsTrue();
        await Assert.That(second.ListCalls).IsEqualTo(otherCalls);
    }

    [TUnit.Core.Test]
    public async Task Real_SDK_tool_list_notification_refreshes_the_changed_source_only()
    {
        await using var upstream = await DynamicMcpCatalogHost.StartAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddMcpClient("weather", upstream.Client));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        upstream.Description = "Updated atmospheric precipitation";
        await upstream.NotifyAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await gateway.ListToolsAsync(guard.Token)).Single().Description != upstream.Description)
        { await Task.Delay(10, guard.Token); }
        await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue();
    }
}
