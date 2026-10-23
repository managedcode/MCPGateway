using System.Text.Json;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGraphProfileBoundaryTests
{
    private const string StoryToolNodeId = "https://managedcode.com/mcpgateway/knowledge/tools/stories/story_item_search/";

    [Test]
    [Arguments("facet")]
    [Arguments("excluded-type")]
    [Arguments("required-type")]
    public async Task Hybrid_search_does_not_restore_tools_rejected_by_structural_schema_filters(string constraint)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            AddStoryTools(options);
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm,
                TypeFilters = constraint == "required-type" ? ["owl:Class"] : ["schema:Article"],
                ExcludedTypes = constraint == "excluded-type" ? ["schema:Article"] : [],
                FacetFilters = constraint == "facet" ? [new("rdf:type", "owl:Class")] : []
            };
        });
        var schemaResult = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("story"));
        var result = await provider.GetRequiredService<IMcpGateway>().SearchAsync("story");

        await Assert.That(schemaResult.Matches).IsEmpty();
        await Assert.That(result.Matches).IsEmpty();
        await Assert.That(result.UsedSchemaSearch).IsTrue();
        await Assert.That(result.UsedSchemaFallback).IsFalse();
    }

    [Test]
    [Arguments("path")]
    [Arguments("query")]
    public async Task Federation_does_not_allow_endpoint_path_or_query_case_variants(string component)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            AddStoryTools(options);
            options.AddMarkdownLdFederatedServiceEndpoint(new("http://127.0.0.1:1/SPARQL?tenant=Private"));
        });
        var result = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("story")
        {
            UseFederation = true,
            IncludeLocalGatewayGraph = false,
            ServiceEndpoints = [component == "path"
                ? "http://127.0.0.1:1/sparql?tenant=Private"
                : "http://127.0.0.1:1/SPARQL?tenant=private"]
        });

        await Assert.That(result.Matches).IsEmpty();
        await Assert.That(result.ServiceEndpointSpecifiers).IsEmpty();
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "graph_federation_endpoint_blocked")).IsTrue();
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "graph_search_failed")).IsFalse();
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "graph_federation_no_endpoints")).IsTrue();
    }

    [Test]
    public async Task Hybrid_merge_keeps_only_the_primary_tools_selected_by_a_valid_relationship_facet()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            AddStoryTools(options);
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TypeFilters = ["schema:Article"],
                ExcludedTypes = [],
                FacetFilters = [new("kb:relatedTo", "https://managedcode.com/mcpgateway/knowledge/tools/stories/story_item_detail/")],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm
            };
        });
        var direct = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("story"));
        var result = await provider.GetRequiredService<IMcpGateway>().SearchAsync("story");

        await Assert.That(direct.Matches).HasSingleItem();
        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].ToolId).IsEqualTo("story_item_search");
        await Assert.That(result.Matches[0].ToolId).IsEqualTo(direct.Matches[0].ToolMatch!.ToolId);
    }

    [Test]
    [Arguments("story")]
    [Arguments("STORY_ITEM_SEARCH")]
    public async Task Valid_profile_constraints_preserve_real_graph_search_and_invocation(string query)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            AddStoryTools(options);
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TypeFilters = ["schema:Article"],
                ExcludedTypes = ["owl:Class"],
                FacetFilters = [new("rdf:type", "schema:Article")],
                CandidateNodeIds = [StoryToolNodeId],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm
            };
        });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var result = await gateway.SearchAsync(query);
        var invocation = await gateway.InvokeAsync(new(ToolId: "story_item_search",
            Arguments: new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("incident", McpGatewayJsonSerializer.Options) }));

        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].ToolId).IsEqualTo("story_item_search");
        await Assert.That(result.UsedSchemaSearch).IsTrue();
        await Assert.That(result.UsedSchemaFallback).IsFalse();
        await Assert.That(invocation.IsSuccess).IsTrue();
        await Assert.That(invocation.Output).IsEqualTo("incident");
    }

    [Test]
    [Arguments("unrestricted")]
    [Arguments("empty")]
    [Arguments("selected")]
    public async Task Schema_inspection_and_graph_tool_preserve_all_configured_constraint_fields(string pool)
    {
        var profile = new KnowledgeGraphSchemaSearchProfile
        {
            TextPredicates = [new("schema:name")],
            RelationshipPredicates = [new("kb:relatedTo", ["schema:name"])
            {
                Direction = KnowledgeGraphSchemaRelationshipDirection.Inbound,
                PredicatePath = ["kb:relatedTo", "kb:nextStep"]
            }],
            ExpansionPredicates = [],
            TypeFilters = ["schema:Article"],
            ExcludedTypes = ["owl:Class"],
            FacetFilters = [new("rdf:type", "schema:Article")],
            CandidateNodeIds = pool switch { "empty" => [], "selected" => [StoryToolNodeId], _ => null }
        };
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            AddStoryTools(options);
            options.MarkdownLdGraphSchemaSearchProfile = profile;
        });
        var schema = await provider.GetRequiredService<IMcpGatewayGraphSearch>().DescribeGraphSchemaAsync();

        await Assert.That(schema.ExcludedTypes).IsEquivalentTo(profile.ExcludedTypes);
        await Assert.That(schema.FacetFilters).IsEquivalentTo([new McpGatewayGraphSchemaFacetFilter("rdf:type", "schema:Article")]);
        await Assert.That(schema.RelationshipPredicates[0].PredicatePath).IsEquivalentTo(profile.RelationshipPredicates[0].PredicatePath);
        await Assert.That(schema.RelationshipPredicates[0].Direction).IsEqualTo("Inbound");
        if (profile.CandidateNodeIds is null)
        {
            await Assert.That(schema.CandidateNodeIds).IsNull();
        }
        else
        {
            await Assert.That(schema.CandidateNodeIds!).IsEquivalentTo(profile.CandidateNodeIds);
        }

        var graphTool = (Microsoft.Extensions.AI.AIFunction)provider.GetRequiredService<McpGatewayToolSet>()
            .CreateGraphTools().Single(tool => tool.Name == McpGatewayToolSet.DefaultGraphSchemaToolName);
        var toolResult = (System.Text.Json.JsonElement)(await graphTool.InvokeAsync(new()))!;
        await Assert.That(toolResult.GetProperty("facetFilters")[0].GetProperty("predicateId").GetString()).IsEqualTo("rdf:type");
        await Assert.That(toolResult.GetProperty("excludedTypes")[0].GetString()).IsEqualTo("owl:Class");
        await Assert.That(toolResult.GetProperty("relationshipPredicates")[0].GetProperty("predicatePath")
            .EnumerateArray().Select(element => element.GetString()!)).IsEquivalentTo(schema.RelationshipPredicates[0].PredicatePath);
        var hasCandidates = toolResult.TryGetProperty("candidateNodeIds", out var candidates);
        if (profile.CandidateNodeIds is null)
        {
            await Assert.That(hasCandidates).IsFalse();
        }
        else
        {
            await Assert.That(hasCandidates).IsTrue();
            await Assert.That(candidates.EnumerateArray().Select(element => element.GetString()!)).IsEquivalentTo(profile.CandidateNodeIds);
        }
    }

    private static void AddStoryTools(McpGatewayOptions options)
    {
        options.AddTool("stories", TestFunctionFactory.CreateFunction(
            (string query) => query, "story_item_search", "Discover story feed records."));
        options.AddTool("stories", TestFunctionFactory.CreateFunction(
            (string query) => query, "story_item_detail", "Inspect a story item search result."));
    }
}
