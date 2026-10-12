using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayFactoryGovernanceTests
{
    [Test]
    public async Task Managed_factory_instance_has_its_own_publication_admission_and_live_result_boundary()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(static _ => { });
        services.AddMcpGatewayInMemoryCatalogStore();
        services.AddMcpGatewayInMemoryResultStore();
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        services.AddSingleton<IMcpGatewayAccessPolicy, Policy>();
        await using var provider = services.BuildServiceProvider();
        await using var instance = provider.GetRequiredService<IMcpGatewayFactory>().Create(options =>
        {
            options.Governance.ManagedCatalog = true;
            options.Governance.MaximumResponseBytes = 512;
            options.Execution.Sources.Add("factory", new() { MaxConcurrency = 1, QueueCapacity = 0 });
            options.AddTool("factory", AIFunctionFactory.Create(static () => new { id = "A123", blob = new string('x', 2000) },
                new AIFunctionFactoryOptions { Name = "large_result" }));
        });
        var publication = instance.Publication!;
        var approved = (await publication.InspectCandidateAsync()).Select(item => item with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await Assert.That((await publication.PublishAsync(approved, 0, "owner")).IsPublished).IsTrue();
        await Assert.That((await provider.GetRequiredService<IMcpGateway>().ListToolsAsync()).Count).IsEqualTo(0);
        await Assert.That((await instance.Discovery.GetToolSchemaAsync(approved.Single().ToolId))!.Tool.DefinitionHash).IsEqualTo(approved.Single().DefinitionHash);
        var result = await instance.Gateway.InvokeAsync(new(ToolId: approved.Single().ToolId)
        { ResponseOptions = new() { StoreOversizedResult = true } });
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.ResultHandle).IsNotNull();
        var settings = new McpGatewayResponseOptions { SelectPaths = ["/id"] };
        await Assert.That((await instance.ResultReader!.ReadAsync(result.ResultHandle!, settings))!.Result.GetProperty("id").GetString()).IsEqualTo("A123");
        // The host's compatibility-mode reader must also honor the managed origin of this stored result.
        var hostReader = provider.GetRequiredService<IMcpGatewayResultReader>();
        await Assert.That(await hostReader.ReadAsync(result.ResultHandle!, new() { MaxBytes = 32_000 })).IsNull();
        await Assert.That(await instance.ResultReader.ReadAsync(result.ResultHandle!, new() { MaxBytes = 32_000 })).IsNull();
        await Assert.That(await hostReader.ReadAsync(result.ResultHandle!, settings)).IsNotNull();
        await publication.RevokeAsync(approved.Single().ToolId, 1);
        await Assert.That(await hostReader.ReadAsync(result.ResultHandle!, settings)).IsNull();
        await Assert.That(await instance.ResultReader.ReadAsync(result.ResultHandle!, settings)).IsNull();
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("owner", "tenant", "all", "v1"));
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(true));
    }
}
