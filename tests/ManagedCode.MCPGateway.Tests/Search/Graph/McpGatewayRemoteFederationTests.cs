using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Datasets;
using VDS.RDF.Writing;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayRemoteFederationTests
{
    [Test]
    public async Task Explicit_remote_endpoint_executes_real_sparql_and_keeps_remote_graph_identity_and_evidence()
    {
        using var remoteGraph = KnowledgeGraph.LoadJsonLd("""
            {"@context":{"schema":"https://schema.org/"},"@graph":[
              {"@id":"https://remote.example.com/tools/story/","@type":"schema:Article",
               "schema:name":"Remote story search","schema:description":"Discover remote story records."}
            ]}
            """).ToOntologySnapshot();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var requests = 0;
        server.MapMethods("/SPARQL", ["GET", "POST"], async context =>
        {
            Interlocked.Increment(ref requests);
            var queryText = context.Request.Method == "GET"
                ? context.Request.Query["query"].ToString()
                : (await context.Request.ReadFormAsync(context.RequestAborted))["query"].ToString();
            var query = new SparqlQueryParser().ParseFromString(queryText);
            var results = (SparqlResultSet)new LeviathanQueryProcessor(new InMemoryDataset(remoteGraph))
                .ProcessQuery(query);
            using var writer = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            new SparqlJsonWriter().Save(results, writer);
            context.Response.ContentType = "application/sparql-results+json";
            await context.Response.WriteAsync(writer.ToString(), context.RequestAborted);
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/SPARQL?tenant=Private");
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(
                (string query) => query, "local_story_search", "Discover local story records."));
            options.AddMarkdownLdFederatedServiceEndpoint(endpoint);
            options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
            {
                TextPredicates = [new("schema:name"), new("schema:description")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TypeFilters = ["schema:Article"],
                ExcludedTypes = [],
                TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm
            };
        });
        var local = await provider.GetRequiredService<IMcpGateway>().SearchAsync("local_story_search");
        await Assert.That(local.Matches).HasSingleItem();
        await Assert.That(requests).IsEqualTo(0);
        var result = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("story")
        {
            UseFederation = true,
            IncludeLocalGatewayGraph = false,
            ServiceEndpoints = [endpoint.AbsoluteUri.Replace("http:", "HTTP:", StringComparison.Ordinal)]
        });

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].NodeId).IsEqualTo("https://remote.example.com/tools/story/");
        await Assert.That(result.Matches[0].ToolMatch).IsNull();
        await Assert.That(result.Matches[0].Evidence.Any(evidence => evidence.ServiceEndpoint == endpoint.AbsoluteUri)).IsTrue();
        await Assert.That(result.Matches[0].Evidence.All(evidence => evidence.SourceContexts.Count == 0)).IsTrue();
        await Assert.That(result.ServiceEndpointSpecifiers).IsEquivalentTo([endpoint.AbsoluteUri]);
        await Assert.That(requests).IsGreaterThan(0);
        await server.StopAsync();
    }
}
