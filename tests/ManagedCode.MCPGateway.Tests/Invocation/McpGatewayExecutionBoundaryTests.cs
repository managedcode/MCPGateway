using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

[TUnit.Core.NotInParallel]
public sealed class McpGatewayExecutionBoundaryTests
{
    [TUnit.Core.Test]
    public async Task Uncooperative_mutation_returns_unknown_but_holds_capacity_and_catalog_until_real_completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = Create(async (CancellationToken _) => { entered.SetResult(); await release.Task; return "committed"; },
            source => { source.ExecutionTimeout = TimeSpan.FromMilliseconds(100); source.MaxConcurrency = 1; source.QueueCapacity = 0; });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var invocation = gateway.InvokeAsync(new(ToolName: "operation"));
        Task? reset = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.UnknownCompletion);
            await Assert.That((await gateway.InvokeAsync(new(ToolName: "operation"))).Outcome).IsEqualTo(McpGatewayInvocationOutcome.RateLimited);
            reset = provider.GetRequiredService<IMcpGatewayCatalogRuntime>().ClearAsync().AsTask();
            await Assert.That(reset.IsCompleted).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await invocation;
            if (reset is not null)
            { await reset; }
        }
    }
    [TUnit.Core.Test]
    public async Task Source_capacity_is_bounded_and_released_after_completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = Create(async (CancellationToken ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return "ok"; },
            source => { source.MaxConcurrency = 1; source.QueueCapacity = 0; });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var first = gateway.InvokeAsync(new(ToolName: "operation"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var overflow = await gateway.InvokeAsync(new(ToolName: "operation"));
            await Assert.That(overflow.Outcome).IsEqualTo(McpGatewayInvocationOutcome.RateLimited);
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.That((await first).IsSuccess).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task Mutation_timeout_reports_unknown_completion_and_never_retries()
    {
        var dispatches = 0;
        await using var provider = Create(async (CancellationToken ct) =>
        { Interlocked.Increment(ref dispatches); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "unreachable"; },
            source => source.ExecutionTimeout = TimeSpan.FromMilliseconds(30));
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "operation"));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.UnknownCompletion);
        await Assert.That(dispatches).IsEqualTo(1);
    }

    [TUnit.Core.Test]
    public async Task Host_approved_read_only_timeout_has_confirmed_timed_out_outcome()
    {
        await using var provider = Create(async (CancellationToken ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "unreachable"; }, source => source.ExecutionTimeout = TimeSpan.FromMilliseconds(30), readOnly: true);
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "operation"));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.TimedOut);
    }

    [TUnit.Core.Test]
    public async Task Business_argument_exception_after_dispatch_is_not_an_invalid_argument_preflight()
    {
        var dispatches = 0;
        await using var provider = Create((CancellationToken _) =>
        { Interlocked.Increment(ref dispatches); throw new ArgumentException("SEEDED_PRIVATE_EXCEPTION"); });
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "operation"));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.UpstreamUnavailable);
        await Assert.That(result.Error!).DoesNotContain("SEEDED_PRIVATE_EXCEPTION");
        await Assert.That(dispatches).IsEqualTo(1);
    }

    [TUnit.Core.Test]
    public async Task Circuit_stops_repeated_failed_dispatches_without_retrying_mutations()
    {
        var dispatches = 0;
        await using var provider = Create((CancellationToken _) =>
        { Interlocked.Increment(ref dispatches); throw new IOException("Upstream connection dropped."); }, source => source.FailureThreshold = 2);
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.InvokeAsync(new(ToolName: "operation"));
        await gateway.InvokeAsync(new(ToolName: "operation"));
        var blocked = await gateway.InvokeAsync(new(ToolName: "operation"));
        await Assert.That(blocked.Outcome).IsEqualTo(McpGatewayInvocationOutcome.RateLimited);
        await Assert.That(dispatches).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task Reconfiguration_waits_for_an_in_flight_invocation_lease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = Create(async (CancellationToken ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return "completed"; });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var invocation = gateway.InvokeAsync(new(ToolName: "operation"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reset = provider.GetRequiredService<IMcpGatewayCatalogRuntime>().ClearAsync().AsTask();
        try
        {
            await Assert.That(reset.IsCompleted).IsFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.That((await invocation).IsSuccess).IsTrue();
        await reset;
        await Assert.That((await gateway.ListToolsAsync()).Count).IsEqualTo(0);
    }

    private static ServiceProvider Create(Func<CancellationToken, Task<string>> operation,
        Action<McpGatewaySourceExecutionOptions>? configure = null, bool readOnly = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.Execution.Enabled = true;
            var limits = new McpGatewaySourceExecutionOptions();
            configure?.Invoke(limits);
            options.Execution.Sources.Add("operations", limits);
            options.AddTool("operations", AIFunctionFactory.Create(operation, new AIFunctionFactoryOptions { Name = "operation" }));
        });
        services.AddSingleton<IMcpGatewayExecutionContextProvider, HostContext>();
        services.AddSingleton<IMcpGatewayAccessPolicy>(new HostPolicy(readOnly));
        return services.BuildServiceProvider();
    }
    private sealed class HostContext : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant-a", "operations", "v1"));
    }
    private sealed class HostPolicy(bool readOnly) : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(true) { IsReadOnly = readOnly });
    }
}
