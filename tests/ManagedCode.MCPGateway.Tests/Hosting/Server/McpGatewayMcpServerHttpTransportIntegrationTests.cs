#pragma warning disable MCPEXP002

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerHttpTransportIntegrationTests
{
    [Test]
    public async Task WithMcpGatewayCatalog_CanHostFiveIsolatedGatewaysOnOneHttpHost()
    {
        await using var host = await MultiGatewayHttpHost.StartAsync();
        var clients = new List<HostedGatewayClient>(host.Routes.Count);

        try
        {
            foreach (var route in host.Routes)
            {
                clients.Add(new HostedGatewayClient(route, await host.CreateClientAsync(route.GatewayId)));
            }

            var results = await Task.WhenAll(
                clients.Select(static client => ExerciseGatewayAsync(client.Route, client.Client))
            );

            await Assert.That(results.Length).IsEqualTo(host.Routes.Count);

            foreach (var result in results)
            {
                await Assert.That(result.ToolNames).IsEquivalentTo(
                    [MultiGatewayHttpHost.ExportedSharedToolName]
                );
                await Assert.That(result.Outputs).IsEquivalentTo(
                [
                    $"{result.GatewayId}:ping-1",
                    $"{result.GatewayId}:ping-2",
                    $"{result.GatewayId}:ping-3",
                ]
                );
            }

            var allOutputs = results.SelectMany(static result => result.Outputs).ToArray();
            await Assert.That(allOutputs.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(15);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.Client.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task WithMcpGatewayCatalog_RequiresPerRouteAuthorizationOnHttpEndpoints()
    {
        await using var host = await MultiGatewayHttpHost.StartAsync();
        var protectedRoute = host.GetRoute("gateway-1");
        var otherRoute = host.GetRoute("gateway-2");
        await using var authorizedClient = await host.CreateClientAsync(protectedRoute.GatewayId);

        var tools = await authorizedClient.ListToolsAsync();
        using var anonymousClient = host.CreateHttpClient(
            protectedRoute.GatewayId,
            includeAuthorizationHeader: false
        );
        using var wrongTokenClient = host.CreateHttpClient(
            protectedRoute.GatewayId,
            otherRoute.Token
        );

        using var anonymousResponse = await SendProbeRequestAsync(
            anonymousClient,
            protectedRoute.RoutePattern
        );
        using var wrongTokenResponse = await SendProbeRequestAsync(
            wrongTokenClient,
            protectedRoute.RoutePattern
        );

        await Assert.That(tools.Select(static tool => tool.Name)).IsEquivalentTo(
            [MultiGatewayHttpHost.ExportedSharedToolName]
        );
        await Assert.That(anonymousResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(wrongTokenResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task WithMcpGatewayCatalog_ComposesExistingHttpSessionHandler()
    {
        var userHandlerCalled = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(static _ => { });
        services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.RunSessionHandler = (_, _, _) =>
                {
                    userHandlerCalled = true;
                    return Task.CompletedTask;
                };
            })
            .WithMcpGatewayCatalog();
        await using var serviceProvider = services.BuildServiceProvider();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var transportOptions = serviceProvider
            .GetRequiredService<IOptions<HttpServerTransportOptions>>()
            .Value;

        await transportOptions.RunSessionHandler!(
            new DefaultHttpContext { RequestServices = serviceProvider },
            gatewayServer.Server,
            CancellationToken.None
        );

        await Assert.That(userHandlerCalled).IsTrue();
    }

    private static async Task<GatewayExerciseResult> ExerciseGatewayAsync(
        MultiGatewayHttpHost.HostedGatewayRoute route,
        McpClient client
    )
    {
        var tools = await client.ListToolsAsync();
        var outputs = await Task.WhenAll(
            Enumerable.Range(1, 3).Select(index => InvokeGatewayAsync(client, $"ping-{index}"))
        );

        return new GatewayExerciseResult(
            route.GatewayId,
            tools.Select(static tool => tool.Name).ToArray(),
            outputs
        );
    }

    private static async Task<string> InvokeGatewayAsync(McpClient client, string query)
    {
        var result = await client.CallToolAsync(
            MultiGatewayHttpHost.ExportedSharedToolName,
            new Dictionary<string, object?> { ["query"] = query }
        );

        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    private static Task<HttpResponseMessage> SendProbeRequestAsync(HttpClient client, string route) =>
        client.PostAsync(route, new StringContent("{}", Encoding.UTF8, "application/json"));

    private sealed record HostedGatewayClient(
        MultiGatewayHttpHost.HostedGatewayRoute Route,
        McpClient Client
    );

    private sealed record GatewayExerciseResult(
        string GatewayId,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> Outputs
    );
}

#pragma warning restore MCPEXP002
