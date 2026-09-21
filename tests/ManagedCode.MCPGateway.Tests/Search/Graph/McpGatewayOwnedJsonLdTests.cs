using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    private const string OwnedToolUriPrefix = "https://catalog.example.com/tools/";
    private const string OwnedGraphResource = "owned-tools.jsonld";

    [TUnit.Core.Test]
    public async Task Owned_jsonld_node_uris_preserve_graph_search_and_exact_invocation()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            ConfigureSearchTools(options);
            options.UseJsonLdGraphResource(typeof(McpGatewaySearchTests).Assembly,
                OwnedGraphResource, descriptor => new Uri(OwnedToolUriPrefix + descriptor.ToolName + "/"));
        });
        var gateway = provider.GetRequiredService<IMcpGateway>();

        var built = await gateway.BuildIndexAsync();
        var search = await gateway.SearchAsync("temperature forecast by city", maxResults: 1);
        var invocation = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: search.Matches.Single().ToolId,
            Arguments: new Dictionary<string, object?> { ["query"] = "Paris" }));
        var exported = await provider.GetRequiredService<IMcpGatewayGraphSearch>().ExportMarkdownLdGraphAsync();

        await Assert.That(built.IsGraphSearchEnabled).IsTrue();
        await Assert.That(search.RankingMode).IsEqualTo("graph");
        await Assert.That(search.Matches.Single().ToolName).IsEqualTo("weather_search_forecast");
        await Assert.That(invocation.IsSuccess).IsTrue();
        await Assert.That(invocation.Output).IsEqualTo("weather:Paris");
        await Assert.That(exported.JsonLd).Contains(OwnedToolUriPrefix);
        await Assert.That(exported.JsonLd).Contains("Resource-owned aurora metadata.");
    }

    [TUnit.Core.Test]
    [TUnit.Core.Arguments("relative")]
    [TUnit.Core.Arguments("duplicate")]
    [TUnit.Core.Arguments("missing")]
    public async Task Invalid_owned_node_bindings_fail_without_regenerating_graph(string mode)
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            ConfigureSearchTools(options);
            options.UseJsonLdGraphResource(typeof(McpGatewaySearchTests).Assembly,
                OwnedGraphResource, descriptor => mode switch
                {
                    "relative" => new Uri(descriptor.ToolName, UriKind.Relative),
                    "duplicate" => new Uri(OwnedToolUriPrefix + "github_search_issues/"),
                    _ => new Uri(OwnedToolUriPrefix + "missing/" + descriptor.ToolName)
                });
        });

        var built = await provider.GetRequiredService<IMcpGateway>().BuildIndexAsync();

        await Assert.That(built.IsGraphSearchEnabled).IsFalse();
        await Assert.That(built.GraphNodeCount).IsEqualTo(0);
        await Assert.That(built.Diagnostics.Count).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task Selecting_default_resource_clears_previous_owned_uri_bindings()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            ConfigureSearchTools(options);
            options.UseJsonLdGraphResource(typeof(McpGatewaySearchTests).Assembly,
                OwnedGraphResource, descriptor => new Uri(OwnedToolUriPrefix + descriptor.ToolName + "/"));
            options.UseJsonLdGraphResource(typeof(McpGatewaySearchTests).Assembly, "embedded-tools.jsonld");
        });

        var built = await provider.GetRequiredService<IMcpGateway>().BuildIndexAsync();

        await Assert.That(built.IsGraphSearchEnabled).IsTrue();
    }
}
