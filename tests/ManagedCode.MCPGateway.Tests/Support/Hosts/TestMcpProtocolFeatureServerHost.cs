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

internal sealed class TestMcpProtocolFeatureServerHost : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _serverTask;

    public const string PromptName = "repository_picker_prompt";
    public const string PromptArgumentName = "repository";
    public const string ResourceName = "repository_overview";
    public const string ResourceUri = "docs://repository/overview";
    public const string ResourceTemplateName = "repository_owner_detail";
    public const string ResourceTemplateUri = "docs://repositories/{owner}";
    public const string ResourceTemplateArgumentName = "owner";

    private TestMcpProtocolFeatureServerHost(
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

    public static async Task<TestMcpProtocolFeatureServerHost> StartAsync(
        CancellationToken cancellationToken = default
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        var builder = services.AddMcpServer(static options =>
        {
            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Resources ??= new ResourcesCapability();
            options.Capabilities.Resources.Subscribe = true;
        });

        builder
            .WithPrompts<TestProtocolPrompts>()
            .WithResources<TestProtocolResources>()
            .WithCompleteHandler(ProtocolFeatureState.CompleteAsync);

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
                ProtocolVersion = McpGatewayMcpProtocolConstants.CurrentProtocolVersion,
                ClientInfo = new Implementation
                {
                    Name = "managedcode-mcpgateway-protocol-tests",
                    Version = "1.0.0",
                },
            },
            loggerFactory,
            cancellationToken
        );

        return new TestMcpProtocolFeatureServerHost(
            serviceProvider,
            client,
            server,
            serverCancellation,
            serverTask
        );
    }

    public Task EmitResourceUpdatedAsync(
        string resourceUri = ResourceUri,
        CancellationToken cancellationToken = default
    ) =>
        Server.SendNotificationAsync(
            NotificationMethods.ResourceUpdatedNotification,
            new ResourceUpdatedNotificationParams { Uri = resourceUri },
            McpJsonUtilities.DefaultOptions,
            cancellationToken
        );

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

    [McpServerPromptType]
    private sealed class TestProtocolPrompts
    {
        [McpServerPrompt(Name = PromptName, Title = "Repository picker")]
        [Description("Builds a prompt that works with a chosen repository name.")]
        public static GetPromptResult BuildRepositoryPrompt(
            [Description("Repository name.")] string repository
        ) =>
            new()
            {
                Description = "Repository picker prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Review repository '{repository}'.",
                        },
                    },
                ],
            };
    }

    [McpServerResourceType]
    private sealed class TestProtocolResources
    {
        [McpServerResource(
            UriTemplate = ResourceUri,
            Name = ResourceName,
            Title = "Repository overview",
            MimeType = "text/markdown"
        )]
        [Description("Returns repository overview markdown.")]
        public static TextResourceContents GetRepositoryOverview() =>
            new()
            {
                Uri = ResourceUri,
                MimeType = "text/markdown",
                Text = "# ManagedCode.MCPGateway\n\nProtocol feature test resource.",
            };

        [McpServerResource(
            UriTemplate = ResourceTemplateUri,
            Name = ResourceTemplateName,
            Title = "Repository owner detail",
            MimeType = "application/json"
        )]
        [Description("Returns detail for a repository owner.")]
        public static TextResourceContents GetRepositoryOwnerDetail(
            [Description("Repository owner.")] string owner
        ) =>
            new()
            {
                Uri = $"docs://repositories/{owner}",
                MimeType = "application/json",
                Text = $$"""{"owner":"{{owner}}","project":"MCPGateway"}""",
            };
    }

    private static class ProtocolFeatureState
    {
        private static readonly string[] PromptCompletionValues =
        [
            "ManagedCode/MCPGateway",
            "ManagedCode/AIBase",
            "ModelContextProtocol/csharp-sdk",
        ];

        private static readonly string[] ResourceTemplateCompletionValues =
        [
            "managedcode",
            "modelcontextprotocol",
            "openai",
        ];

        public static ValueTask<CompleteResult> CompleteAsync(
            RequestContext<CompleteRequestParams> request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedPrefix = request.Params?.Argument?.Value ?? string.Empty;
            var matches = request.Params?.Ref switch
            {
                PromptReference promptReference
                    when string.Equals(promptReference.Name, PromptName, StringComparison.Ordinal)
                        && string.Equals(
                            request.Params?.Argument?.Name,
                            PromptArgumentName,
                            StringComparison.Ordinal
                        ) => PromptCompletionValues,
                ResourceTemplateReference resourceReference
                    when string.Equals(
                            resourceReference.Uri,
                            ResourceTemplateUri,
                            StringComparison.Ordinal
                        )
                        && string.Equals(
                            request.Params?.Argument?.Name,
                            ResourceTemplateArgumentName,
                            StringComparison.Ordinal
                        ) => ResourceTemplateCompletionValues,
                _ => [],
            };

            var values = matches
                .Where(value =>
                    value.StartsWith(requestedPrefix, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            return ValueTask.FromResult(
                new CompleteResult
                {
                    Completion = new Completion
                    {
                        Values = values,
                        Total = values.Count,
                        HasMore = false,
                    },
                }
            );
        }

    }
}
