using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayFullTextIntegrationTests
{
    [TUnit.Core.Test]
    public async Task Real_Lucene_graph_tools_reuse_index_and_refresh_after_catalog_reconfiguration()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddTool("public",
            AIFunctionFactory.Create(static () => "ok", new AIFunctionFactoryOptions { Name = "weather", Description = "Weather precipitation forecast" })));
        var search = provider.GetRequiredService<IMcpGatewayGraphFullTextSearch>();
        await Assert.That((await search.BuildFullTextIndexAsync()).TripleCount > 0).IsTrue();
        var first = await search.SearchFullTextAsync("precipitation");
        var second = await search.SearchFullTextAsync("precipitation");
        await Assert.That(first.Count > 0).IsTrue();
        await Assert.That(first.Select(match => match.NodeId).SequenceEqual(second.Select(match => match.NodeId))).IsTrue();
        var tools = provider.GetRequiredService<McpGatewayFullTextToolSet>().CreateTools();
        var toolResult = await ((AIFunction)tools.Single(tool => tool.Name == McpGatewayFullTextToolSet.SearchToolName))
            .InvokeAsync(new AIFunctionArguments { ["query"] = "precipitation" });
        await Assert.That(toolResult).IsNotNull();
        await provider.GetRequiredService<IMcpGatewayCatalogRuntime>().ClearAsync();
        await Assert.That((await search.SearchFullTextAsync("precipitation")).Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task Private_graph_metadata_is_excluded_before_Lucene_indexing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.AddTool("public", AIFunctionFactory.Create(static () => "ok", new AIFunctionFactoryOptions { Name = "weather", Description = "Weather forecast" }));
            options.AddTool("private", AIFunctionFactory.Create(static () => "secret", new AIFunctionFactoryOptions { Name = "vault", Description = "SECRETSEED payroll salaries" }));
        });
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        services.AddSingleton<IMcpGatewayAccessPolicy, PublicPolicy>();
        await using var provider = services.BuildServiceProvider();
        var search = provider.GetRequiredService<IMcpGatewayGraphFullTextSearch>();
        await Assert.That((await search.SearchFullTextAsync("SECRETSEED")).Count).IsEqualTo(0);
        await Assert.That((await search.SearchFullTextAsync("weather")).Count > 0).IsTrue();
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "public", "v1"));
    }
    private sealed class PublicPolicy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(request.SourceId != "private"));
    }
}
