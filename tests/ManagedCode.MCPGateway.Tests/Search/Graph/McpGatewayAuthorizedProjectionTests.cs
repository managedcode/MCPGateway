using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayAuthorizedProjectionTests
{
    [Test]
    public async Task Application_owned_package_generated_graph_is_projected_before_SPARQL_export_and_Lucene()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.AddTool("public", TestFunctionFactory.CreateFunction(static (string query) => query, "weather", "Public precipitation forecast"));
            options.AddTool("private", TestFunctionFactory.CreateFunction(static () => "secret", "vault", "SEEDED_PRIVATE_PAYROLL metadata"));
            options.UseMarkdownLdGraphDocuments(descriptors => McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors));
        });
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        services.AddSingleton<IMcpGatewayAccessPolicy, Policy>();
        services.AddSingleton<IMcpGatewayAuthorizedGraphFactory, Projection>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var graph = provider.GetRequiredService<IMcpGatewayGraphSearch>();
        var search = await gateway.SearchAsync("precipitation");
        await Assert.That(search.Matches.Single().ToolId).IsEqualTo("weather");
        var export = await graph.ExportMarkdownLdGraphAsync();
        await Assert.That(export.JsonLd + export.Turtle + export.DotGraph + export.MermaidFlowchart).DoesNotContain("SEEDED_");
        await Assert.That((await graph.SearchGraphAsync(new("SEEDED_PRIVATE_PAYROLL"))).Matches).IsEmpty();
        await Assert.That((await provider.GetRequiredService<IMcpGatewayGraphFullTextSearch>().SearchFullTextAsync("SEEDED_PRIVATE_PAYROLL")).Count).IsEqualTo(0);
        await Assert.That(await provider.GetRequiredService<IMcpGatewayDiscovery>().GetToolSchemaAsync("vault")).IsNull();
    }
    private sealed class Projection : IMcpGatewayAuthorizedGraphFactory
    {
        private const string ArticleType = "https://schema.org/Article";
        public ValueTask<KnowledgeGraph> CreateAsync(McpGatewayExecutionContext context, KnowledgeGraph source,
            IReadOnlyList<McpGatewayToolDescriptor> visibleTools, CancellationToken cancellationToken = default)
        {
            var subjects = McpGatewayMarkdownLdGraphFile.CreateDocuments(visibleTools).Select(document => document.CanonicalUri!)
                .ToHashSet(StringComparer.Ordinal);
            return ValueTask.FromResult(source.Project(new(subjects)
            { AllowedResourceObjectIds = subjects.Append(ArticleType).ToHashSet(StringComparer.Ordinal) }));
        }
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "public", "v1"));
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(request.SourceId != "private"));
    }
}
