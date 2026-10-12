using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGovernedPublicationTests
{
    [Test]
    public async Task Approved_name_is_stable_when_a_normalization_collision_changes_source_membership()
    {
        var context = new HostContext();
        await using var provider = Create(context, options => options.AddTool("source", TestFunctionFactory.CreateFunction(
            static () => "approved", "record-read", "Read records")));
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var definitions = (await publication.InspectCandidateAsync()).Select(item => item with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await publication.PublishAsync(definitions, 0, "owner");
        var name = definitions.Single().ToolId;
        provider.GetRequiredService<IMcpGatewayRegistry>().AddTool("source", TestFunctionFactory.CreateFunction(
            static () => "unreviewed", "record_read", "Unreviewed alternate backend"));
        var candidates = await publication.InspectCandidateAsync();
        await Assert.That(candidates.Single(item => item.UpstreamToolName == "record-read").ToolId).IsEqualTo(name);
        await Assert.That(candidates.Single(item => item.UpstreamToolName == "record_read").ToolId).IsNotEqualTo(name);
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: name))).Output).IsEqualTo("approved");
        await Assert.That((await publication.RevokeAsync(name.ToUpperInvariant(), 1)).IsPublished).IsTrue();
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: name))).Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
    }

    [Test]
    public async Task Bounded_store_rejection_is_distinct_from_a_concurrent_publication_conflict()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.Governance.ManagedCatalog = true;
            options.AddTool("source", TestFunctionFactory.CreateFunction(static () => "a", "first", "First operation"));
            options.AddTool("source", TestFunctionFactory.CreateFunction(static () => "b", "second", "Second operation"));
        });
        services.AddMcpGatewayInMemoryCatalogStore(options => options.MaxDefinitions = 1);
        services.AddSingleton<IMcpGatewayExecutionContextProvider, HostContext>();
        services.AddSingleton<IMcpGatewayAccessPolicy, HostPolicy>();
        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var definitions = (await publisher.InspectCandidateAsync()).Select(item => item with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        var rejected = await publisher.PublishAsync(definitions, 0, "owner");
        await Assert.That(rejected.ErrorCode).IsEqualTo("publication_store_rejected");
        await Assert.That((await provider.GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync()).Revision).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task Draft_tools_are_unavailable_through_direct_meta_and_schema_faithful_paths_after_revocation()
    {
        var context = new HostContext();
        var dispatched = 0;
        await using var provider = Create(context, options => options.AddTool("operations", AIFunctionFactory.Create(
            () => Interlocked.Increment(ref dispatched), new AIFunctionFactoryOptions { Name = "public_status", Description = "Read service status." })));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var drafts = await publication.InspectCandidateAsync();
        await Assert.That(drafts.Single().Lifecycle).IsEqualTo(McpGatewayToolLifecycle.Draft);
        await Assert.That((await gateway.ListToolsAsync()).Count).IsEqualTo(0);
        var toolId = drafts.Single().ToolId;
        var draftCall = await gateway.InvokeAsync(new(ToolId: toolId));
        await Assert.That(draftCall.Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        await Assert.That(dispatched).IsEqualTo(0);
        var published = await publication.PublishAsync(drafts.Select(definition => definition with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray(), 0, "owner");
        await Assert.That(published.IsPublished).IsTrue();
        var proxy = (AIFunction)(await provider.GetRequiredService<McpGatewayDiscoveryToolSet>().CreateSchemaFaithfulToolsAsync([toolId])).Single();
        var first = (JsonElement)(await proxy.InvokeAsync(new AIFunctionArguments()))!;
        await Assert.That(first.GetProperty("isSuccess").GetBoolean()).IsTrue();
        await Assert.That(dispatched).IsEqualTo(1);
        await Assert.That((await publication.RevokeAsync(toolId, 1)).IsPublished).IsTrue();
        var revoked = (JsonElement)(await proxy.InvokeAsync(new AIFunctionArguments()))!;
        await Assert.That(revoked.GetProperty("outcome").GetString()).IsEqualTo(nameof(McpGatewayInvocationOutcome.Denied));
        var meta = (AIFunction)provider.GetRequiredService<McpGatewayToolSet>().CreateTools().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
        var metaResult = (JsonElement)(await meta.InvokeAsync(new AIFunctionArguments { ["toolId"] = toolId }))!;
        await Assert.That(metaResult.GetProperty("isSuccess").GetBoolean()).IsFalse();
        await Assert.That(dispatched).IsEqualTo(1);
        await Assert.That((await gateway.ListToolsAsync()).Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task Concurrent_publication_conflicts_and_content_rollback_preserves_revocations()
    {
        var context = new HostContext();
        await using var provider = Create(context, options => options.AddTool("operations", TestFunctionFactory.CreateFunction(static () => "ok", "public_status", "Read status.")));
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var approved = (await publication.InspectCandidateAsync()).Select(definition => definition with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        var attempts = await Task.WhenAll(publication.PublishAsync(approved, 0, "first-owner"), publication.PublishAsync(approved, 0, "second-owner"));
        await Assert.That(attempts.Count(result => result.IsPublished)).IsEqualTo(1);
        await Assert.That(attempts.Single(result => !result.IsPublished).ErrorCode).IsEqualTo("publication_revision_conflict");
        await Assert.That((await publication.RevokeAsync(approved[0].ToolId, 1)).IsPublished).IsTrue();
        await Assert.That((await publication.PublishAsync(approved, 2, "rollback-owner")).IsPublished).IsTrue();
        var store = await provider.GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync();
        await Assert.That(store.Revision).IsEqualTo(3);
        await Assert.That(store.RevokedToolIds.Contains(approved[0].ToolId)).IsTrue();
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: approved[0].ToolId))).Outcome)
            .IsEqualTo(McpGatewayInvocationOutcome.Denied);
    }

    [TUnit.Core.Test]
    public async Task Failed_candidate_preserves_published_revision_and_new_tools_need_review()
    {
        var context = new HostContext();
        await using var provider = Create(context, options => options.AddTool("operations", TestFunctionFactory.CreateFunction(static () => "ok", "public_status", "Read status.")));
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var first = (await publication.InspectCandidateAsync()).Select(definition => definition with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await publication.PublishAsync(first, 0, "owner");
        provider.GetRequiredService<IMcpGatewayRegistry>().AddTool("operations", TestFunctionFactory.CreateFunction(static () => "new", "public_new", "New unreviewed operation."));
        var invalid = await publication.PublishAsync([first[0] with { DefinitionHash = "forged" }], 1, "owner");
        await Assert.That(invalid.IsPublished).IsFalse();
        await Assert.That((await provider.GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync()).Revision).IsEqualTo(1);
        var visible = await provider.GetRequiredService<IMcpGateway>().ListToolsAsync();
        await Assert.That(visible.Count).IsEqualTo(1);
        await Assert.That(visible.Single().ToolId).IsEqualTo(first[0].ToolId);
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "operations_public_new"))).Outcome)
            .IsEqualTo(McpGatewayInvocationOutcome.Denied);
    }

    [TUnit.Core.Test]
    public async Task User_context_cannot_impersonate_admin_and_private_metadata_never_enters_search_or_cache()
    {
        var context = new HostContext();
        await using var provider = Create(context, options =>
        {
            options.AddTool("operations", TestFunctionFactory.CreateFunction(static () => "public", "public_weather", "Weather forecast for users."));
            options.AddTool("operations", TestFunctionFactory.CreateFunction(static () => "private", "private_weather", "SEEDED_PRIVATE_METADATA weather forecast."));
        });
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var approved = (await publication.InspectCandidateAsync()).Select(definition => definition with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await publication.PublishAsync(approved, 0, "owner");
        var gateway = provider.GetRequiredService<IMcpGateway>();
        _ = await gateway.SearchAsync("weather forecast");
        context.Current = new("alice", "tenant-a", "public", "v1");
        var searched = await gateway.SearchAsync(new McpGatewaySearchRequest("weather forecast", Context:
            new Dictionary<string, object?> { ["role"] = "admin", ["tenant"] = "tenant-b" }));
        await Assert.That(JsonSerializer.Serialize(searched, McpGatewayJsonSerializer.Options)).DoesNotContain("SEEDED_PRIVATE_METADATA");
        await Assert.That(searched.Matches.All(match => match.ToolId.Contains("public", StringComparison.Ordinal))).IsTrue();
        var denied = await gateway.InvokeAsync(new(ToolId: approved.Single(tool => tool.ToolId.Contains("private", StringComparison.Ordinal)).ToolId,
            Context: new Dictionary<string, object?> { ["subject"] = "admin" }));
        await Assert.That(denied.Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        var graph = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("weather forecast"));
        await Assert.That(JsonSerializer.Serialize(graph, McpGatewayJsonSerializer.Options)).DoesNotContain("SEEDED_PRIVATE_METADATA");
        var exportDenied = false;
        try
        {
            await provider.GetRequiredService<IMcpGatewayGraphSearch>().ExportMarkdownLdGraphAsync();
        }
        catch (UnauthorizedAccessException)
        {
            exportDenied = true;
        }
        await Assert.That(exportDenied).IsTrue();
    }

    [Test]
    public async Task Reviewed_permissions_change_the_definition_hash_and_gate_schema_and_dispatch()
    {
        var context = new HostContext();
        var calls = 0;
        await using var provider = Create(context, options => options.AddTool("operations", TestFunctionFactory.CreateFunction(
            () => { Interlocked.Increment(ref calls); return "ok"; }, "public_status", "Read status.")));
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var candidate = (await publication.InspectCandidateAsync()).Single();
        var reviewed = candidate.WithReviewMetadata(["review.read", "review.read"], "review-42") with { Lifecycle = McpGatewayToolLifecycle.Approved };
        await Assert.That(reviewed.RequiredPermissions.Length).IsEqualTo(1);
        await Assert.That(reviewed.DefinitionHash == candidate.DefinitionHash).IsFalse();
        var detached = reviewed.GetProtocolTool();
        detached.Description = "unreviewed mutation";
        await Assert.That(reviewed.GetProtocolTool().Description).IsEqualTo("Read status.");
        await Assert.That((await publication.PublishAsync([reviewed], 0, "admin")).IsPublished).IsTrue();
        var schema = await provider.GetRequiredService<IMcpGatewayDiscovery>().GetToolSchemaAsync(reviewed.ToolId);
        await Assert.That(schema!.Tool.RequiredPermissions.Single()).IsEqualTo("review.read");
        context.Current = new("alice", "tenant-a", "public", "v1");
        await Assert.That(await provider.GetRequiredService<IMcpGatewayDiscovery>().GetToolSchemaAsync(reviewed.ToolId)).IsNull();
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: reviewed.ToolId))).Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        await Assert.That(calls).IsEqualTo(0);
        context.Current = new("admin", "tenant-a", "all", "v1");
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: reviewed.ToolId))).IsSuccess).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    private static ServiceProvider Create(HostContext context, Action<McpGatewayOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => { options.Governance.ManagedCatalog = true; configure(options); });
        services.AddMcpGatewayInMemoryCatalogStore();
        services.AddMcpGatewayInMemorySearchCache();
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(context);
        services.AddSingleton<IMcpGatewayAccessPolicy, HostPolicy>();
        return services.BuildServiceProvider();
    }

    private sealed class HostContext : IMcpGatewayExecutionContextProvider
    {
        public McpGatewayExecutionContext Current { get; set; } = new("admin", "tenant-a", "all", "v1");
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }

    private sealed class HostPolicy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.RequiredPermissions.Contains("review.read") && context.SubjectId != "admin")
            { return ValueTask.FromResult(McpGatewayAccessDecision.Denied); }
            var allowed = context.SubjectId == "admin" || request.Operation == McpGatewayOperation.ReadGraph
                || (request.Operation is McpGatewayOperation.Discover or McpGatewayOperation.Invoke or McpGatewayOperation.InspectSchema
                    && request.AssetId.Contains("public", StringComparison.Ordinal));
            return ValueTask.FromResult(new McpGatewayAccessDecision(allowed) { IsReadOnly = true });
        }
    }
}
