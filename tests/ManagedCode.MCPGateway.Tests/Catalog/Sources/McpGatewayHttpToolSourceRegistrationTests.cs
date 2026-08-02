#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayHttpToolSourceRegistrationTests
{
    [Test]
    public async Task CreateTransportOptions_UsesStreamableHttpAndSdkHeaderOptions()
    {
        var endpoint = new Uri("https://example.com/mcp");
        var transportOptions = McpGatewayHttpToolSourceRegistration.CreateTransportOptions(
            "docs",
            endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer token",
                [" X-Custom-Header "] = "custom",
                ["Empty"] = "",
            }
        );

        await Assert.That(transportOptions.Endpoint).IsEqualTo(endpoint);
        await Assert.That(transportOptions.Name).IsEqualTo("docs");
        await Assert.That(transportOptions.TransportMode).IsEqualTo(HttpTransportMode.StreamableHttp);
        await Assert.That(transportOptions.AdditionalHeaders).IsNotNull();
        await Assert.That(transportOptions.AdditionalHeaders!.Count).IsEqualTo(2);
        await Assert.That(transportOptions.AdditionalHeaders["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(transportOptions.AdditionalHeaders["X-Custom-Header"]).IsEqualTo("custom");
    }

    [Test]
    public async Task CreateTransportOptions_UsesCurrentHttpServerOptions()
    {
        var oauth = new ClientOAuthOptions
        {
            RedirectUri = new Uri("https://example.com/oauth/callback"),
            ClientId = "client-id",
        };
        var options = new McpGatewayHttpServerOptions
        {
            SourceId = "current-http",
            Endpoint = new Uri("https://example.com/mcp"),
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer token",
            },
            ConnectionTimeout = TimeSpan.FromSeconds(7),
            OAuth = oauth,
        };

        var transportOptions = McpGatewayHttpToolSourceRegistration.CreateTransportOptions(options);

        await Assert.That(transportOptions.Endpoint).IsEqualTo(options.Endpoint);
        await Assert.That(transportOptions.Name).IsEqualTo("current-http");
        await Assert.That(transportOptions.TransportMode).IsEqualTo(HttpTransportMode.StreamableHttp);
        await Assert.That(transportOptions.AdditionalHeaders!["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(transportOptions.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(ReferenceEquals(transportOptions.OAuth, oauth)).IsTrue();
    }

    [Test]
    public async Task AddHttpServer_LoadsToolsThroughHttpMcpSourceWithHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Test-Gateway"] = "streamable-http",
        };
        await using var upstreamServer = await HttpMcpServerHost.StartAsync(headers);
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
            options.AddHttpServer("http-upstream", upstreamServer.Endpoint, headers)
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var build = await gateway.BuildIndexAsync();
        await Assert.That(build.Diagnostics).IsEmpty();
        var tools = await gateway.ListToolsAsync();
        var tool = tools.Single(static descriptor =>
            descriptor.ToolId == "streamable_http_lookup"
        );

        await Assert.That(tool.SourceKind).IsEqualTo(McpGatewaySourceKind.HttpMcp);
        await Assert.That(tool.SourceId).IsEqualTo("http-upstream");
        await Assert.That(tool.ToolName).IsEqualTo("streamable_http_lookup");
    }
}
