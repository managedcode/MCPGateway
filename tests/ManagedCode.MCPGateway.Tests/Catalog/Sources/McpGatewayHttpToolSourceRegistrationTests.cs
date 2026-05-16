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
            McpGatewayHttpToolSourceRegistration.DefaultTransportMode,
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
    public async Task CreateTransportOptions_UsesConfiguredTransportMode()
    {
        var transportOptions = McpGatewayHttpToolSourceRegistration.CreateTransportOptions(
            "legacy",
            new Uri("https://example.com/mcp"),
            HttpTransportMode.AutoDetect,
            headers: null
        );

        await Assert.That(transportOptions.TransportMode).IsEqualTo(HttpTransportMode.AutoDetect);
    }

    [Test]
    public async Task CreateTransportOptions_UsesConfiguredHttpServerOptions()
    {
        var oauth = new ClientOAuthOptions
        {
            RedirectUri = new Uri("https://example.com/oauth/callback"),
            ClientId = "client-id",
        };
        var options = new McpGatewayHttpServerOptions
        {
            SourceId = "sessioned",
            Endpoint = new Uri("https://example.com/mcp"),
            TransportMode = HttpTransportMode.Sse,
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer token",
            },
            ConnectionTimeout = TimeSpan.FromSeconds(7),
            KnownSessionId = "session-1",
            OwnsSession = false,
            OAuth = oauth,
            MaxReconnectionAttempts = 9,
            DefaultReconnectionInterval = TimeSpan.FromMilliseconds(250),
        };

        var transportOptions = McpGatewayHttpToolSourceRegistration.CreateTransportOptions(options);

        await Assert.That(transportOptions.Endpoint).IsEqualTo(options.Endpoint);
        await Assert.That(transportOptions.Name).IsEqualTo("sessioned");
        await Assert.That(transportOptions.TransportMode).IsEqualTo(HttpTransportMode.Sse);
        await Assert.That(transportOptions.AdditionalHeaders!["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(transportOptions.ConnectionTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(transportOptions.KnownSessionId).IsEqualTo("session-1");
        await Assert.That(transportOptions.OwnsSession).IsFalse();
        await Assert.That(ReferenceEquals(transportOptions.OAuth, oauth)).IsTrue();
        await Assert.That(transportOptions.MaxReconnectionAttempts).IsEqualTo(9);
        await Assert
            .That(transportOptions.DefaultReconnectionInterval)
            .IsEqualTo(TimeSpan.FromMilliseconds(250));
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

        var tools = await gateway.ListToolsAsync();
        var tool = tools.Single(static descriptor =>
            descriptor.ToolId == "http-upstream:streamable_http_lookup"
        );

        await Assert.That(tool.SourceKind).IsEqualTo(McpGatewaySourceKind.HttpMcp);
        await Assert.That(tool.SourceId).IsEqualTo("http-upstream");
        await Assert.That(tool.ToolName).IsEqualTo("streamable_http_lookup");
    }
}
