#pragma warning disable MCPEXP001

using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestMcpTaskFeatureServerHost : IAsyncDisposable
{
    public const string OptionalToolName = "optional_background_tool";
    public const string RequiredToolName = "required_background_tool";
    public const string CancellableToolName = "cancellable_background_tool";

    private readonly ServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _serverTask;

    private TestMcpTaskFeatureServerHost(
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

    private ModelContextProtocol.Server.McpServer Server { get; }

    public static async Task<TestMcpTaskFeatureServerHost> StartAsync(
        CancellationToken cancellationToken = default
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));

        var builder = services.AddMcpServer(options =>
        {
            options.TaskStore = new InMemoryMcpTaskStore();
            options.SendTaskStatusNotifications = true;
            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Tasks ??= new McpTasksCapability
            {
                Requests = new RequestMcpTasksCapability
                {
                    Tools = new ToolsMcpTasksCapability
                    {
                        Call = new CallToolMcpTasksCapability(),
                    },
                },
                List = new ListMcpTasksCapability(),
                Cancel = new CancelMcpTasksCapability(),
            };
        });

        builder.WithTools<TestTaskTools>();

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
                    Name = "managedcode-mcpgateway-task-tests",
                    Version = "1.0.0",
                },
            },
            loggerFactory,
            cancellationToken
        );

        return new TestMcpTaskFeatureServerHost(
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

    [McpServerToolType]
    private sealed class TestTaskTools
    {
        [McpServerTool(
            Name = OptionalToolName,
            Title = "Optional task tool",
            ReadOnly = true,
            Idempotent = true,
            TaskSupport = ToolTaskSupport.Optional,
            UseStructuredContent = false
        )]
        [Description("Runs a short optional background task.")]
        public static async Task<string> RunOptionalToolAsync(
            [Description("Task payload.")] string value,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            return $"optional:{value}";
        }

        [McpServerTool(
            Name = RequiredToolName,
            Title = "Required task tool",
            ReadOnly = true,
            Idempotent = true,
            TaskSupport = ToolTaskSupport.Required,
            UseStructuredContent = false
        )]
        [Description("Runs a short task that must be invoked through the tasks surface.")]
        public static async Task<string> RunRequiredToolAsync(
            [Description("Task payload.")] string value,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            return $"required:{value}";
        }

        [McpServerTool(
            Name = CancellableToolName,
            Title = "Cancellable task tool",
            ReadOnly = true,
            Idempotent = true,
            TaskSupport = ToolTaskSupport.Optional,
            UseStructuredContent = false
        )]
        [Description("Runs a cancellable task.")]
        public static async Task<string> RunCancellableToolAsync(
            [Description("Task payload.")] string value,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return $"cancellable:{value}";
        }
    }
}

#pragma warning restore MCPEXP001
