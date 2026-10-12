using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class DynamicMcpCatalogHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _stop;
    private readonly Task _serverTask;
    public McpClient Client { get; }
    public ModelContextProtocol.Server.McpServer Server { get; }
    public string Description { get; set; } = "Weather forecast";
    public bool RejectListing { get; set; }
    public bool EmptyTools { get; set; }
    public bool RejectInvocationArguments { get; set; }
    public int ListCalls;
    public int InvokeCalls;
    public TaskCompletionSource? ListingEntered { get; set; }
    public Task? ListingRelease { get; set; }

    private DynamicMcpCatalogHost(ServiceProvider services, McpClient client, ModelContextProtocol.Server.McpServer server, Task task,
        CancellationTokenSource stop)
    { _services = services; Client = client; Server = server; _serverTask = task; _stop = stop; }

    public static async Task<DynamicMcpCatalogHost> StartAsync()
    {
        DynamicMcpCatalogHost? host = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer(options => options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } })
            .WithListToolsHandler(async (_, ct) =>
            {
                Interlocked.Increment(ref host!.ListCalls);
                host.ListingEntered?.TrySetResult();
                if (host.ListingRelease is { } release)
                { await release.WaitAsync(ct); }
                if (host.RejectListing)
                { throw new ModelContextProtocol.McpException("SEEDED_PRIVATE_LIST_FAILURE"); }
                return new ListToolsResult
                {
                    Tools = host.EmptyTools ? [] : [new Tool { Name = "lookup", Description = host.Description,
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } }) }]
                };
            })
            .WithCallToolHandler((request, _) =>
            {
                Interlocked.Increment(ref host!.InvokeCalls);
                if (host.RejectInvocationArguments)
                { throw new ModelContextProtocol.McpProtocolException("SEEDED_PRIVATE_VALIDATION", ModelContextProtocol.McpErrorCode.InvalidParams); }
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = host.Description }] });
            });
        var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var inbound = new Pipe();
        var outbound = new Pipe();
        var server = ModelContextProtocol.Server.McpServer.Create(new StreamServerTransport(inbound.Reader.AsStream(), outbound.Writer.AsStream()),
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value, loggerFactory, provider);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(inbound.Writer.AsStream(), outbound.Reader.AsStream(), loggerFactory));
        host = new(provider, client, server, running, stop);
        return host;
    }

    public Task NotifyAsync() => Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification,
        new ToolListChangedNotificationParams(), cancellationToken: CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await McpTestServerShutdown.AwaitServerStopAsync(_serverTask, _stop.Token);
        _stop.Dispose();
        await _services.DisposeAsync();
    }
}
