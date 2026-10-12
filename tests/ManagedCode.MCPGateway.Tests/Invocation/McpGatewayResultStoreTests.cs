using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResultStoreTests
{
    [TUnit.Core.Test]
    public async Task Stored_approved_result_requires_same_subject_tenant_partition_and_policy_revision()
    {
        var services = new ServiceCollection();
        services.AddMcpGatewayInMemoryResultStore();
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IMcpGatewayResultStore>();
        var actor = new McpGatewayExecutionContext("alice", "tenant", "visible", "v1");
        var stored = await store.StoreAsync(actor, JsonSerializer.SerializeToElement(new { status = "approved" }));
        await Assert.That(stored).IsNotNull();
        await Assert.That(await store.ReadAsync(actor, stored!.Handle)).IsNotNull();
        foreach (var other in new[] { actor with { SubjectId = "bob" }, actor with { TenantId = "other" },
            actor with { AuthorizationPartition = "other" }, actor with { PolicyRevision = "v2" }, actor with { IsAuthenticated = false } })
        {
            await Assert.That(await store.ReadAsync(other, stored.Handle)).IsNull();
            await Assert.That(await store.RemoveAsync(other, stored.Handle)).IsFalse();
        }
        await Assert.That(await store.RemoveAsync(actor, stored.Handle)).IsTrue();
        await Assert.That(await store.ReadAsync(actor, stored.Handle)).IsNull();
    }

    [TUnit.Core.Test]
    public async Task Capacity_is_bounded_and_expiry_releases_capacity_before_next_write()
    {
        var clock = new ResultClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMcpGatewayInMemoryResultStore(options => { options.MaxEntries = 1; options.TimeToLive = TimeSpan.FromMinutes(1); });
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IMcpGatewayResultStore>();
        var actor = new McpGatewayExecutionContext("alice", "tenant", "visible", "v1");
        var json = JsonSerializer.SerializeToElement(new { id = "A" });
        var first = await store.StoreAsync(actor, json);
        await Assert.That(first).IsNotNull();
        await Assert.That(await store.StoreAsync(actor, json)).IsNull();
        clock.Advance(TimeSpan.FromMinutes(2));
        var next = await store.StoreAsync(actor, json);
        await Assert.That(next).IsNotNull();
        await Assert.That(await store.ReadAsync(actor, first!.Handle)).IsNull();
        await Assert.That(await store.ReadAsync(actor, next!.Handle)).IsNotNull();
    }

    [TUnit.Core.Test]
    public async Task Oversized_invocation_stores_complete_approved_JSON_and_never_partial_JSON()
    {
        var actor = new McpGatewayExecutionContext("alice", "tenant", "visible", "v1");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => options.AddTool(AIFunctionFactory.Create(static () => new { value = new string('x', 500) },
            new AIFunctionFactoryOptions { Name = "large_result" })));
        services.AddMcpGatewayInMemoryResultStore();
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(new ContextProvider(actor));
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "large_result")
        { ResponseOptions = new() { MaxBytes = 64, StoreOversizedResult = true } });
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Output).IsNull();
        await Assert.That(result.ResultHandle).IsNotNull();
        var saved = await provider.GetRequiredService<IMcpGatewayResultStore>().ReadAsync(actor, result.ResultHandle!);
        await Assert.That(saved!.Result.GetProperty("value").GetString()!.Length).IsEqualTo(500);
        await Assert.That(await provider.GetRequiredService<IMcpGatewayResultStore>()
            .ReadAsync(actor with { SubjectId = "bob" }, result.ResultHandle!)).IsNull();
    }

    [Test]
    public async Task Total_byte_rejection_releases_reservation_and_per_result_limit_stays_independent()
    {
        var services = new ServiceCollection();
        services.AddMcpGatewayInMemoryResultStore(options => { options.MaxStoredBytes = 100; options.MaxResultBytes = 64; });
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IMcpGatewayResultStore>();
        var actor = new McpGatewayExecutionContext("alice", "tenant", "visible", "v1");
        var json = JsonSerializer.SerializeToElement(new string('x', 60));
        var first = await store.StoreAsync(actor, json);
        await Assert.That(first).IsNotNull();
        await Assert.That(await store.StoreAsync(actor, json)).IsNull();
        await Assert.That(await store.StoreAsync(actor, JsonSerializer.SerializeToElement(new string('x', 65)))).IsNull();
        await Assert.That(await store.RemoveAsync(actor, first!.Handle)).IsTrue();
        var next = await store.StoreAsync(actor, json);
        await Assert.That(next).IsNotNull();
        await Assert.That((await store.ReadAsync(actor, next!.Handle))!.Result.GetString()).IsEqualTo(new string('x', 60));
    }

    [Test]
    public async Task Legacy_store_adapters_cannot_accept_managed_metadata_or_originating_instance_caps()
    {
        IMcpGatewayResultStore store = new LegacyStore();
        var actor = new McpGatewayExecutionContext("alice", "tenant", "visible", "v1");
        var json = JsonSerializer.SerializeToElement(new { id = "A" });
        var tool = new McpGatewayToolDescriptor("tool", "source", McpGatewaySourceKind.Local,
            new ModelContextProtocol.Protocol.Tool { Name = "tool", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }, [])
        { RequiresPublication = true };
        await Assert.That(await store.StoreAsync(actor, json, tool)).IsNull();
        await Assert.That(await store.StoreAsync(actor, json, tool, 128)).IsNull();
        await Assert.That(((LegacyStore)store).Writes).IsEqualTo(0);
        await Assert.That(await store.StoreAsync(actor, json, tool with { RequiresPublication = false })).IsNotNull();
        await Assert.That(((LegacyStore)store).Writes).IsEqualTo(1);
    }

    private sealed class LegacyStore : IMcpGatewayResultStore
    {
        public int Writes;
        public ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result, CancellationToken cancellationToken = default)
        { Writes++; return ValueTask.FromResult<McpGatewayStoredResult?>(new("handle", result, DateTimeOffset.UtcNow.AddMinutes(1))); }
        public ValueTask<McpGatewayStoredResult?> ReadAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default) => ValueTask.FromResult<McpGatewayStoredResult?>(null);
        public ValueTask<bool> RemoveAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class ContextProvider(McpGatewayExecutionContext context) : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(context);
    }

    private sealed class ResultClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
