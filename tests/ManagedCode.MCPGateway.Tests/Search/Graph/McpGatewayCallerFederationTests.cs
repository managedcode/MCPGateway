using System.Net;
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

public sealed class McpGatewayCallerFederationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Managed_federation_uses_caller_transport_and_rejects_stale_credentials_before_HTTP(bool staleCredentials)
    {
        using var graph = KnowledgeGraph.LoadJsonLd("""
            {"@context":{"schema":"https://schema.org/"},"@graph":[
              {"@id":"https://remote.example/story","@type":"schema:Article","schema:name":"Remote story search"}]}
            """).ToOntologySnapshot();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var requests = 0;
        server.MapMethods("/sparql", ["GET", "POST"], async context =>
        {
            await Assert.That(context.Request.Headers["X-Caller"].ToString()).IsEqualTo("alice");
            Interlocked.Increment(ref requests);
            var text = context.Request.Method == "GET" ? context.Request.Query["query"].ToString()
                : (await context.Request.ReadFormAsync(context.RequestAborted))["query"].ToString();
            var result = (SparqlResultSet)new LeviathanQueryProcessor(new InMemoryDataset(graph))
                .ProcessQuery(new SparqlQueryParser().ParseFromString(text));
            using var writer = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            new SparqlJsonWriter().Save(result, writer);
            context.Response.ContentType = "application/sparql-results+json";
            await context.Response.WriteAsync(writer.ToString(), context.RequestAborted);
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        var actor = new Actor();
        var resolver = new Resolver(actor, staleCredentials);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGatewayInMemoryCatalogStore();
        services.AddMcpGateway(options =>
        {
            options.Governance.ManagedCatalog = true;
            options.AddTool("local", TestFunctionFactory.CreateFunction(static () => "ok", "story", "Story search"));
            options.AddMarkdownLdFederatedServiceEndpoint(endpoint);
            options.FederatedServiceBindings.Add(new(endpoint, "remote", "story-reader", "stories", ["read"]));
            options.MarkdownLdGraphSchemaSearchProfile = new()
            {
                TextPredicates = [new("schema:name")],
                RelationshipPredicates = [],
                ExpansionPredicates = [],
                TypeFilters = [],
                ExcludedTypes = [],
            };
        });
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor);
        services.AddSingleton<IMcpGatewayAccessPolicy, Policy>();
        services.AddSingleton<IMcpGatewayFederatedClientResolver>(resolver);
        await using var provider = services.BuildServiceProvider();
        var publication = provider.GetRequiredService<IMcpGatewayCatalogPublication>();
        var approved = (await publication.InspectCandidateAsync()).Select(item => item with { Lifecycle = McpGatewayToolLifecycle.Approved }).ToArray();
        await Assert.That((await publication.PublishAsync(approved, 0, "owner")).IsPublished).IsTrue();
        var result = await provider.GetRequiredService<IMcpGatewayGraphSearch>().SearchGraphAsync(new("story")
        { UseFederation = true, IncludeLocalGatewayGraph = false });
        await Assert.That(resolver.Disposed).IsEqualTo(1);
        if (staleCredentials)
        {
            await Assert.That(requests).IsEqualTo(0);
            await Assert.That(result.Diagnostics).IsNotEmpty();
            await Assert.That(result.Matches).IsEmpty();
        }
        else
        {
            await Assert.That(requests).IsGreaterThan(0);
            await Assert.That(result.Diagnostics).IsEmpty();
            await Assert.That(result.Matches.Single().NodeId).IsEqualTo("https://remote.example/story");
        }
        await server.StopAsync();
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "reader", "v1") { CredentialRevision = "c1" });
    }
    private sealed class Policy : IMcpGatewayAccessPolicy, IMcpGatewayEgressPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(context.SubjectId == "alice"));
        public ValueTask<bool> IsAllowedAsync(McpGatewayExecutionContext context, McpGatewayEgressRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(context.SubjectId == "alice"
                && (request.ResolvedAddress is null || IPAddress.IsLoopback(request.ResolvedAddress)));
    }
    private sealed class Resolver(Actor actor, bool stale) : IMcpGatewayFederatedClientResolver
    {
        public int Disposed;
        public ValueTask<IMcpGatewayFederatedClientLease> ResolveAsync(McpGatewayExecutionContext context,
            McpGatewayFederatedServiceBinding binding, CancellationToken cancellationToken = default)
        {
            var client = McpGatewayEgressHttpClient.Create(binding.SourceId, new Policy(), actor, new SocketsHttpHandler());
            client.DefaultRequestHeaders.Add("X-Caller", context.SubjectId);
            return ValueTask.FromResult<IMcpGatewayFederatedClientLease>(new Lease(this, client, binding, stale ? "stale" : context.CredentialRevision));
        }
    }
    private sealed class Lease(Resolver owner, HttpClient client, McpGatewayFederatedServiceBinding binding, string revision) : IMcpGatewayFederatedClientLease
    {
        public HttpClient Client => client;
        public string BindingReference => binding.BindingReference;
        public string Audience => binding.Audience;
        public string CredentialRevision => revision;
        public IReadOnlySet<string> GrantedScopes { get; } = new HashSet<string> { "read" };
        public ValueTask DisposeAsync() { client.Dispose(); Interlocked.Increment(ref owner.Disposed); return ValueTask.CompletedTask; }
    }
}
