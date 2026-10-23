using System.Text.Json;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySchemaCandidateFlowTests
{
    [Test]
    [Arguments("selected")]
    [Arguments("empty")]
    [Arguments("unknown")]
    [Arguments("invalid")]
    public async Task Explicit_profile_candidate_pool_is_preserved_and_validated_by_the_owner(string mode)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_search", "Discover story feed records."));
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_detail", "Inspect a story item search result."));
            options.UseSchemaAwareMarkdownLdGraphSearch();
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TypeFilters = ["schema:Article"],
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm,
                CandidateNodeIds = mode switch
                {
                    "selected" => ["https://managedcode.com/mcpgateway/knowledge/tools/stories/story_item_detail/"],
                    "unknown" => ["https://example.com/not-a-tool"],
                    "invalid" => ["relative"],
                    _ => []
                }
            };
        });
        var graphs = provider.GetRequiredService<IMcpGatewayGraphSearch>();
        var result = await graphs.SearchGraphAsync(new(mode == "selected" ? "story" : "story_item_search"));

        if (mode == "selected")
        {
            await Assert.That(result.Matches).HasSingleItem();
            await Assert.That(result.Matches[0].ToolMatch?.ToolId).IsEqualTo("story_item_detail");
        }
        else
        {
            await Assert.That(result.Matches.Count).IsEqualTo(0);
        }
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "graph_search_failed"))
            .IsEqualTo(mode == "invalid");
    }

    [Test]
    [Arguments("selected")]
    [Arguments("empty")]
    [Arguments("unknown")]
    public async Task Hybrid_search_keeps_the_explicit_primary_pool_during_ranking_and_fallback(string mode)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_search", "Discover story feed records."));
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_detail", "Inspect a story item search result."));
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TypeFilters = ["schema:Article"],
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm,
                CandidateNodeIds = mode switch
                {
                    "selected" => ["https://managedcode.com/mcpgateway/knowledge/tools/stories/story_item_detail/"],
                    "unknown" => ["https://example.com/not-a-tool"],
                    _ => []
                }
            };
        });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var result = await gateway.SearchAsync("story");
        await Assert.That(result.UsedSchemaSearch).IsTrue();
        await Assert.That(result.UsedSchemaFallback).IsFalse();
        await Assert.That(result.Matches.Count).IsEqualTo(mode == "selected" ? 1 : 0);
        if (mode == "selected")
        {
            await Assert.That(result.Matches[0].ToolId).IsEqualTo("story_item_detail");
        }
    }

    [Test]
    public async Task Exact_candidate_preserves_evidence_and_expansion_to_another_tool_on_the_shared_graph()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_search", "Discover story feed records."));
            options.AddTool("stories", TestFunctionFactory.CreateFunction(
                (string query) => query, "story_item_detail", "Inspect a story item search result."));
            options.MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.SchemaAware;
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TypeFilters = ["schema:Article"],
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [new("kb:relatedTo", ["schema:description"])],
                ExpansionPredicates =
                [
                    new("kb:relatedTo", KnowledgeGraphSchemaSearchRole.Related, 0.8),
                    new("kb:nextStep", KnowledgeGraphSchemaSearchRole.NextStep, 0.8)
                ],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm
            };
        });
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var graphs = provider.GetRequiredService<IMcpGatewayGraphSearch>();
        await gateway.BuildIndexAsync();
        var before = await graphs.ExportMarkdownLdGraphAsync();

        var result = await graphs.SearchGraphAsync(new("story_item_search") { MaxResults = 1 });

        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].ToolMatch?.ToolId).IsEqualTo("story_item_search");
        var detail = result.RelatedMatches.Single(match => match.ToolMatch?.ToolId == "story_item_detail");
        await Assert.That(result.Matches[0].Evidence.Any(evidence => evidence.RelatedNodeId == detail.NodeId)).IsTrue();
        await Assert.That(result.NextStepMatches.Any(match => match.ToolMatch?.ToolId == "story_item_detail")).IsTrue();
        await Assert.That(result.GeneratedSparql).Contains("VALUES ?subject");
        var after = await graphs.ExportMarkdownLdGraphAsync();
        await Assert.That(after.NodeCount).IsEqualTo(before.NodeCount);
        await Assert.That(after.EdgeCount).IsEqualTo(before.EdgeCount);
        var invoked = await gateway.InvokeAsync(new(ToolId: "story_item_search", Arguments: new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("incident", McpGatewayJsonSerializer.Options) }));
        await Assert.That(invoked.IsSuccess).IsTrue();
        await Assert.That(invoked.Output).IsEqualTo("incident");
    }

    [Test]
    public async Task Real_mcp_candidate_search_keeps_sdk_invocation_and_full_graph_expansion()
    {
        await using var upstream = await TestMcpServerHost.StartGraphAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            options.AddMcpClient("stories", upstream.Client, disposeClient: false));
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var graphs = provider.GetRequiredService<IMcpGatewayGraphSearch>();
        await gateway.BuildIndexAsync();

        var result = await graphs.SearchGraphAsync(new("story_item_search") { MaxResults = 1 });

        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].ToolMatch?.ToolId).IsEqualTo("story_item_search");
        await Assert.That(result.GeneratedSparql).Contains("VALUES ?subject");
        await Assert.That(result.NextStepMatches.Any(match => match.ToolMatch?.ToolId == "story_item_detail")).IsTrue();
        var invoked = await gateway.InvokeAsync(new(ToolId: "story_item_search", Arguments: new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("incident", McpGatewayJsonSerializer.Options) }));
        await Assert.That(invoked.IsSuccess).IsTrue();
        await Assert.That(invoked.McpResult).IsNotNull();
        await Assert.That(invoked.McpResult!.IsError).IsNotEqualTo(true);
        await Assert.That(invoked.McpResult.Content.Count).IsGreaterThan(0);
        await Assert.That(invoked.McpResult.Content.OfType<TextContentBlock>()
            .Any(content => content.Text.Contains("incident", StringComparison.Ordinal))).IsTrue();
    }
}
