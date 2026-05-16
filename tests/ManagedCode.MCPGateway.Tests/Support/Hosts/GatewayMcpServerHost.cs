using System.IO.Pipelines;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class GatewayMcpServerHost : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _serverTask;

    private GatewayMcpServerHost(
        ServiceProvider serviceProvider,
        McpClient client,
        ModelContextProtocol.Server.McpServer server,
        CancellationTokenSource cancellationTokenSource,
        Task serverTask
    )
    {
        _serviceProvider = serviceProvider;
        Client = client;
        Server = server;
        _cancellationTokenSource = cancellationTokenSource;
        _serverTask = serverTask;
    }

    public McpClient Client { get; }

    public ModelContextProtocol.Server.McpServer Server { get; }

    public IMcpGatewayRegistry Registry =>
        _serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

    public T GetRequiredService<T>()
        where T : notnull => _serviceProvider.GetRequiredService<T>();

    public static async Task<GatewayMcpServerHost> StartAsync(
        Action<McpGatewayOptions> configureGateway,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(configureGateway);

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddMcpGateway(configureGateway);
        configureServices?.Invoke(services);
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
        _cancellationTokenSource.Cancel();

        await McpTestServerShutdown.AwaitServerStopAsync(
            _serverTask,
            _cancellationTokenSource.Token
        );

        await Client.DisposeAsync();
        await Server.DisposeAsync();
        _cancellationTokenSource.Dispose();
        await _serviceProvider.DisposeAsync();
    }
}
