using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestMcpPromptListFeatureServerHost : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _serverTask;
    private readonly McpServerOptions _options;

    private TestMcpPromptListFeatureServerHost(
        ServiceProvider serviceProvider,
        McpClient client,
        ModelContextProtocol.Server.McpServer server,
        CancellationTokenSource cancellationTokenSource,
        Task serverTask,
        McpServerOptions options
    )
    {
        _serviceProvider = serviceProvider;
        Client = client;
        Server = server;
        _cancellationTokenSource = cancellationTokenSource;
        _serverTask = serverTask;
        _options = options;
    }

    public const string InitialPromptName = "repository_picker_prompt";

    public McpClient Client { get; }

    private ModelContextProtocol.Server.McpServer Server { get; }

    public static async Task<TestMcpPromptListFeatureServerHost> StartAsync(
        CancellationToken cancellationToken = default
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));

        var builder = services.AddMcpServer();
        builder.WithPrompts([CreatePrompt(InitialPromptName)]);

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var options = serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Prompts ??= new PromptsCapability();
        options.Capabilities.Prompts.ListChanged = true;

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream()
        );
        var server = ModelContextProtocol.Server.McpServer.Create(
            serverTransport,
            options,
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
                    Name = "managedcode-mcpgateway-prompt-list-tests",
                    Version = "1.0.0",
                },
            },
            loggerFactory,
            cancellationToken
        );

        return new TestMcpPromptListFeatureServerHost(
            serviceProvider,
            client,
            server,
            serverCancellation,
            serverTask,
            options
        );
    }

    public async Task AddPromptAsync(
        string promptName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(_options.PromptCollection);
        _options.PromptCollection.Add(CreatePrompt(promptName));
        await Server.SendNotificationAsync(
            NotificationMethods.PromptListChangedNotification,
            new PromptListChangedNotificationParams(),
            McpJsonUtilities.DefaultOptions,
            cancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();

        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException) { }

        await Client.DisposeAsync();
        await Server.DisposeAsync();
        _cancellationTokenSource.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    private static McpServerPrompt CreatePrompt(string promptName) =>
        McpServerPrompt.Create(
            (Func<GetPromptResult>)(() => new GetPromptResult
            {
                Description = $"{promptName} prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = $"Prompt '{promptName}'." },
                    },
                ],
            }),
            new McpServerPromptCreateOptions
            {
                Name = promptName,
                Title = promptName.Replace('_', ' '),
                Description = $"{promptName} prompt.",
            }
        );
}
