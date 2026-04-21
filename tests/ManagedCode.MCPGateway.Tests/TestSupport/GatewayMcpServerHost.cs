using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class GatewayMcpServerHost(
    ServiceProvider serviceProvider,
    McpClient client,
    ModelContextProtocol.Server.McpServer server,
    CancellationTokenSource cancellationTokenSource,
    Task serverTask
) : IAsyncDisposable
{
    public McpClient Client { get; } = client;

    public static async Task<GatewayMcpServerHost> StartAsync(
        Action<McpGatewayOptions> configureGateway,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(configureGateway);

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddMcpGateway(configureGateway);
        services.AddMcpServer().WithMcpGatewayCatalog();

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var options = serviceProvider.GetRequiredService<IOptions<McpServerOptions>>();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream()
        );
        var server = ModelContextProtocol.Server.McpServer.Create(
            serverTransport,
            options.Value,
            loggerFactory,
            serviceProvider
        );

        var serverCancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(serverCancellation.Token);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory
        );
        var client = await McpClient.CreateAsync(
            clientTransport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "managedcode-mcpgateway-downstream-tests",
                    Version = "1.0.0",
                },
            },
            loggerFactory,
            cancellationToken
        );

        return new GatewayMcpServerHost(
            serviceProvider,
            client,
            server,
            serverCancellation,
            serverTask
        );
    }

    public async ValueTask DisposeAsync()
    {
        cancellationTokenSource.Cancel();

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException) { }

        await Client.DisposeAsync();
        await server.DisposeAsync();
        cancellationTokenSource.Dispose();
        await serviceProvider.DisposeAsync();
    }
}
