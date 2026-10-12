using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayOntologyIntegrationTests
{
    [Test]
    [Arguments(1, false)]
    [Arguments(40, false)]
    [Arguments(40, true)]
    public async Task Ontology_profile_searches_real_ontology_nodes_without_a_tool_only_candidate_pool(int count, bool useExplicitPool)
    {
        var profile = KnowledgeGraphSchemaSearchProfile.Ontology with
        {
            MaxResults = 5,
            CandidateNodeIds = useExplicitPool ? ["urn:managedcode:markdown-ld-kb:vocab:MarkdownDocument"] : null
        };
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            for (var index = 0; index < count; index++)
            {
                options.AddTool("stories", TestFunctionFactory.CreateFunction(
                    (string query) => query, $"story_item_search_{index}", "Discover story records."));
            }
            options.MarkdownLdGraphSchemaSearchProfile = profile;
        });
        var graphs = provider.GetRequiredService<IMcpGatewayGraphSearch>();
        var exported = await graphs.ExportMarkdownLdGraphAsync();
        var ownerResult = await KnowledgeGraph.LoadJsonLd(exported.JsonLd)
            .SearchBySchemaAsync("Markdown", profile);
        var result = await graphs.SearchGraphAsync(new("Markdown"));

        await Assert.That(ownerResult.Matches.Count).IsGreaterThan(0);
        await Assert.That(result.Matches.Select(match => match.NodeId))
            .IsEquivalentTo(ownerResult.Matches.Select(match => match.NodeId));
        await Assert.That(result.Matches.All(match => match.ToolMatch is null)).IsTrue();
        await Assert.That(result.GeneratedSparql!.Contains("VALUES ?subject", StringComparison.Ordinal)).IsEqualTo(useExplicitPool);
    }
}
