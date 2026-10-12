using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayCallerClientIntegrationTests
{
    [TUnit.Core.Test]
    [TUnit.Core.Arguments("audience", "cred-v1", "read", McpGatewayInvocationOutcome.Succeeded)]
    [TUnit.Core.Arguments("other", "cred-v1", "read", McpGatewayInvocationOutcome.Denied)]
    [TUnit.Core.Arguments("audience", "old-credentials", "read", McpGatewayInvocationOutcome.Denied)]
    [TUnit.Core.Arguments("audience", "cred-v1", "other", McpGatewayInvocationOutcome.Denied)]
    public async Task Caller_bound_SDK_client_enforces_audience_revision_and_scopes_before_dispatch(string audience,
        string revision, string scope, McpGatewayInvocationOutcome expected)
    {
        await using var catalog = await DynamicMcpCatalogHost.StartAsync();
        await using var caller = await DynamicMcpCatalogHost.StartAsync();
        var resolver = new Resolver(caller.Client, audience, revision, scope);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        services.AddSingleton<IMcpGatewayExecutionClientResolver>(resolver);
        services.AddMcpGateway(options =>
        {
            options.AddMcpClient("remote", catalog.Client);
            options.Execution.RemoteBindings.Add("remote", new("remote", "reviewed-binding", "audience", ["read"]));
        });
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "lookup"));
        await Assert.That(result.Outcome).IsEqualTo(expected);
        await Assert.That(catalog.InvokeCalls).IsEqualTo(0);
        await Assert.That(caller.InvokeCalls).IsEqualTo(expected == McpGatewayInvocationOutcome.Succeeded ? 1 : 0);
        await Assert.That(resolver.Disposals).IsEqualTo(1);
    }

    [TUnit.Core.Test]
    public async Task Caller_tool_schema_drift_cannot_execute_a_previously_reviewed_binding()
    {
        await using var catalog = await DynamicMcpCatalogHost.StartAsync();
        await using var caller = await DynamicMcpCatalogHost.StartAsync();
        caller.Description = "Unreviewed replacement behavior";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        services.AddSingleton<IMcpGatewayExecutionClientResolver>(new Resolver(caller.Client, "audience", "cred-v1", "read"));
        services.AddMcpGateway(options =>
        {
            options.AddMcpClient("remote", catalog.Client);
            options.Execution.RemoteBindings.Add("remote", new("remote", "reviewed-binding", "audience", ["read"]));
        });
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "lookup"));
        await Assert.That(result.Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        await Assert.That(caller.InvokeCalls).IsEqualTo(0);
        await Assert.That(catalog.InvokeCalls).IsEqualTo(0);
    }
    [Test]
    public async Task Concurrent_actors_dispatch_only_through_their_own_SDK_clients_and_credential_revisions()
    {
        await using var catalog = await DynamicMcpCatalogHost.StartAsync();
        await using var alice = await DynamicMcpCatalogHost.StartAsync();
        await using var bob = await DynamicMcpCatalogHost.StartAsync();
        var actor = new ConcurrentActor();
        var resolver = new ConcurrentResolver(alice.Client, bob.Client);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor);
        services.AddSingleton<IMcpGatewayExecutionClientResolver>(resolver);
        services.AddMcpGateway(options =>
        {
            options.AddMcpClient("remote", catalog.Client);
            options.Execution.RemoteBindings.Add("remote", new("remote", "reviewed-binding", "audience", ["read"]));
        });
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            actor.Subject.Value = index % 2 == 0 ? "alice" : "bob";
            try
            { await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue(); }
            finally { actor.Subject.Value = null; }
        }));
        await Assert.That(alice.InvokeCalls).IsEqualTo(6);
        await Assert.That(bob.InvokeCalls).IsEqualTo(6);
        await Assert.That(catalog.InvokeCalls).IsEqualTo(0);
        await Assert.That(resolver.Disposals).IsEqualTo(12);
    }
    private sealed class ConcurrentActor : IMcpGatewayExecutionContextProvider
    {
        public AsyncLocal<string?> Subject { get; } = new();
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var subject = Subject.Value ?? "catalog";
            return ValueTask.FromResult(new McpGatewayExecutionContext(subject, "tenant", subject, "v1") { CredentialRevision = subject + "-revision" });
        }
    }
    private sealed class ConcurrentResolver(McpClient alice, McpClient bob) : IMcpGatewayExecutionClientResolver
    {
        public int Disposals;
        public ValueTask<IMcpGatewayExecutionClientLease> ResolveAsync(McpGatewayExecutionContext context, McpGatewayRemoteBinding binding,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IMcpGatewayExecutionClientLease>(new ConcurrentLease(this,
                context.SubjectId == "alice" ? alice : bob, context.SubjectId + "-revision"));
    }
    private sealed class ConcurrentLease(ConcurrentResolver owner, McpClient client, string revision) : IMcpGatewayExecutionClientLease
    {
        public McpClient Client => client;
        public string BindingReference => "reviewed-binding";
        public string Audience => "audience";
        public string CredentialRevision => revision;
        public IReadOnlySet<string> GrantedScopes { get; } = new HashSet<string>(StringComparer.Ordinal) { "read" };
        public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); return ValueTask.CompletedTask; }
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "partition", "v1") { CredentialRevision = "cred-v1" });
    }
    private sealed class Resolver(McpClient client, string audience, string revision, string scope) : IMcpGatewayExecutionClientResolver
    {
        public int Disposals;
        public ValueTask<IMcpGatewayExecutionClientLease> ResolveAsync(McpGatewayExecutionContext context, McpGatewayRemoteBinding binding,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IMcpGatewayExecutionClientLease>(new Lease(this, client, audience, revision, scope));
    }
    private sealed class Lease(Resolver owner, McpClient client, string audience, string revision, string scope) : IMcpGatewayExecutionClientLease
    {
        public McpClient Client => client;
        public string BindingReference => "reviewed-binding";
        public string Audience => audience;
        public string CredentialRevision => revision;
        public IReadOnlySet<string> GrantedScopes { get; } = new HashSet<string>(StringComparer.Ordinal) { scope };
        public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); return ValueTask.CompletedTask; }
    }
}
