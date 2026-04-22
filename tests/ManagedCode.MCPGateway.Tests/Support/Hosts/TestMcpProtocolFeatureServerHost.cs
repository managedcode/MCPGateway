using System.Collections.Concurrent;
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
    private readonly ProtocolFeatureState _state;

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
        Task serverTask,
        ProtocolFeatureState state
    )
    {
        _serviceProvider = serviceProvider;
        Client = client;
        Server = server;
        _cancellationTokenSource = cancellationTokenSource;
        _serverTask = serverTask;
        _state = state;
    }

    public McpClient Client { get; }

    private ModelContextProtocol.Server.McpServer Server { get; }

    public static async Task<TestMcpProtocolFeatureServerHost> StartAsync(
        CancellationToken cancellationToken = default
    )
    {
        var state = new ProtocolFeatureState();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        var builder = services.AddMcpServer();

        builder
            .WithPrompts<TestProtocolPrompts>()
            .WithResources<TestProtocolResources>()
            .WithCompleteHandler(ProtocolFeatureState.CompleteAsync)
            .WithSubscribeToResourcesHandler(state.SubscribeAsync)
            .WithUnsubscribeFromResourcesHandler(state.UnsubscribeAsync);

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
            serverTask,
            state
        );
    }

    public Task EmitResourceUpdatedAsync(
        string resourceUri = ResourceUri,
        CancellationToken cancellationToken = default
    ) => _state.EmitResourceUpdatedAsync(resourceUri, cancellationToken);

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

    private sealed class ProtocolFeatureState
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

        private readonly ConcurrentDictionary<SubscriptionKey, McpServer> _subscriptions = new();

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

        public ValueTask<EmptyResult> SubscribeAsync(
            RequestContext<SubscribeRequestParams> request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resourceUri = request.Params?.Uri?.Trim() ?? string.Empty;
            if (resourceUri.Length > 0)
            {
                _subscriptions[SubscriptionKey.Create(request.Server, resourceUri)] = request.Server;
            }

            return ValueTask.FromResult(new EmptyResult());
        }

        public ValueTask<EmptyResult> UnsubscribeAsync(
            RequestContext<UnsubscribeRequestParams> request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resourceUri = request.Params?.Uri?.Trim() ?? string.Empty;
            if (resourceUri.Length > 0)
            {
                _subscriptions.TryRemove(SubscriptionKey.Create(request.Server, resourceUri), out _);
            }

            return ValueTask.FromResult(new EmptyResult());
        }

        public async Task EmitResourceUpdatedAsync(
            string resourceUri,
            CancellationToken cancellationToken
        )
        {
            var subscribers = _subscriptions
                .Where(pair => string.Equals(pair.Key.ResourceUri, resourceUri, StringComparison.Ordinal))
                .Select(static pair => pair.Value)
                .ToArray();

            foreach (var subscriber in subscribers)
            {
                await subscriber.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams { Uri = resourceUri },
                    McpJsonUtilities.DefaultOptions,
                    cancellationToken
                );
            }
        }

        private sealed record SubscriptionKey(string SessionId, string ResourceUri)
        {
            public static SubscriptionKey Create(McpServer server, string resourceUri) =>
                new(server.SessionId ?? string.Empty, resourceUri);
        }
    }
}
