using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayHttpRefreshIntegrationTests
{
    [Test]
    public async Task Streamable_HTTP_subscription_refreshes_an_initially_empty_tool_catalog()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var empty = true;
        var subscribed = new TaskCompletionSource<(ModelContextProtocol.Server.McpServer Server, RequestId Id)>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddMcpServer(options => options.Capabilities = new() { Tools = new() { ListChanged = true } })
            .WithHttpTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = empty ? [] : [new Tool { Name = "lookup", Description = "Updated precipitation forecast",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }) }],
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] }))
            .WithSubscriptionsListenHandler(async (request, ct) =>
            {
                var id = request.JsonRpcRequest.Id;
                var acknowledgement = JsonSerializer.SerializeToNode(new SubscriptionsAcknowledgedNotificationParams
                { Notifications = new() { ToolsListChanged = true } }, McpJsonUtilities.DefaultOptions)!.AsObject();
                acknowledgement[McpGatewayMcpProtocolConstants.MetaEnvelopePropertyName] = McpGatewaySubscriptionMetadata.Create(id);
                await request.Server.SendNotificationAsync(NotificationMethods.SubscriptionsAcknowledgedNotification,
                    acknowledgement, cancellationToken: ct);
                subscribed.TrySetResult((request.Server, id));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new EmptyResult();
            });
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync();
        await using (var provider = GatewayTestServiceProviderFactory.Create(options => options.AddHttpServer("weather", new(server.Urls.Single() + "/mcp"))))
        {
            var gateway = provider.GetRequiredService<IMcpGateway>();
            await Assert.That((await gateway.ListToolsAsync()).Count).IsEqualTo(0);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener = await subscribed.Task.WaitAsync(guard.Token);
            empty = false;
            await listener.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification,
                new ToolListChangedNotificationParams { Meta = McpGatewaySubscriptionMetadata.Create(listener.Id) }, cancellationToken: guard.Token);
            while ((await gateway.ListToolsAsync(guard.Token)).Count == 0)
            { await Task.Delay(10, guard.Token); }
            await Assert.That((await gateway.InvokeAsync(new(ToolName: "lookup"))).IsSuccess).IsTrue();
        }
        await server.StopAsync();
    }
}
