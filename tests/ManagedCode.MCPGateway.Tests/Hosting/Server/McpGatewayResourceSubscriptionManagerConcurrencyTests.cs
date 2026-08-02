#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceSubscriptionManagerConcurrencyTests
{
    private const string ListenerId = "listen:string:concurrency-test";
    private const string SubscriptionId = "concurrency-test";

    [Test]
    public async Task SubscribeAsync_ConcurrentFirstSubscriptionsDoNotLeakPinnedBinding()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var source = new BlockingResourceSource("source-a");
        var bindingDisposeCount = 0;
        var binding = new McpGatewayServerBinding(
            new NoOpGateway(),
            new NoOpPromptCatalog(),
            new StaticResourceCatalog(
                [
                    new McpGatewayResourceDescriptor(
                        "source-a",
                        McpGatewaySourceKind.Local,
                        new Resource
                        {
                            Name = "overview",
                            Title = "overview",
                            Uri = "docs://overview",
                            Description = "Reads overview.",
                            MimeType = "text/plain",
                        }
                    ),
                ],
                []
            ),
            new NoOpRegistry(),
            listSourcesAsync: _ => ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>([source]),
            disposeAsync: () =>
            {
                bindingDisposeCount++;
                return ValueTask.CompletedTask;
            }
        );
        var manager = CreateManager(
            new McpGatewayMcpServerBindingManager(new StaticBindingResolver(binding)),
            serviceProvider
        );

        var firstSubscribe = SubscribeAsync(manager, gatewayServer.Server);
        await source.FirstSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondSubscribe = SubscribeAsync(manager, gatewayServer.Server);

        source.ReleaseFirstSubscription.SetResult(true);

        await firstSubscribe.WaitAsync(TimeSpan.FromSeconds(5));
        await secondSubscribe.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.UnsubscribeAsync(
            gatewayServer.Server,
            "docs://overview",
            ListenerId,
            CancellationToken.None
        );

        await Assert.That(source.SubscriptionCount).IsEqualTo(2);
        await Assert.That(source.DisposedSubscriptionCount).IsEqualTo(2);
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
    }

    private static McpGatewayResourceSubscriptionManager CreateManager(
        McpGatewayMcpServerBindingManager bindingManager,
        IServiceProvider serviceProvider
    )
    {
        var logger = NullLogger<McpGatewayResourceSubscriptionManager>.Instance;
        var registry = new McpGatewayResourceSubscriptionRegistry();
        var cleanup = new McpGatewayResourceSubscriptionCleanup(bindingManager);
        var forwarder = new McpGatewayResourceSubscriptionForwarder(
            registry,
            cleanup,
            logger
        );
        var subscriptionFactory = new McpGatewayResourceSubscriptionFactory(
            bindingManager,
            forwarder,
            serviceProvider,
            NullLoggerFactory.Instance
        );
        var lifetime = new McpGatewayResourceSubscriptionLifetime(registry, cleanup);
        return new McpGatewayResourceSubscriptionManager(
            bindingManager,
            registry,
            cleanup,
            subscriptionFactory,
            lifetime,
            logger
        );
    }

    private static async Task SubscribeAsync(
        McpGatewayResourceSubscriptionManager manager,
        ModelContextProtocol.Server.McpServer server
    )
    {
        var deliveryGate = new McpGatewaySubscriptionDeliveryGate();
        await deliveryGate.OpenAsync(CancellationToken.None);
        await manager.SubscribeAsync(
            requestServices: null,
            server,
            "docs://overview",
            ListenerId,
            new RequestId(SubscriptionId),
            deliveryGate,
            CancellationToken.None
        );
    }

    private sealed class BlockingResourceSource(string sourceId) : IMcpGatewayServerSource
    {
        private int _subscribeCalls;

        public TaskCompletionSource<bool> FirstSubscriptionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<bool> ReleaseFirstSubscription { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int SubscriptionCount { get; private set; }

        public int DisposedSubscriptionCount { get; private set; }

        public string SourceId { get; } = sourceId;

        public ValueTask<CompleteResult?> CompleteAsync(
            Reference reference,
            Argument argument,
            CompleteContext? context,
            IServiceProvider? serviceProvider,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<CompleteResult?>(null);

        public Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
            Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IAsyncDisposable?>(null);

        public async Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            SubscriptionCount++;
            if (Interlocked.Increment(ref _subscribeCalls) == 1)
            {
                FirstSubscriptionStarted.TrySetResult(true);
                await ReleaseFirstSubscription.Task.WaitAsync(cancellationToken);
            }

            return new TrackingSubscription(this);
        }

        private sealed class TrackingSubscription(BlockingResourceSource owner) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.DisposedSubscriptionCount++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StaticBindingResolver(IMcpGatewayServerBinding binding)
        : IMcpGatewayServerBindingResolver
    {
        public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(binding);
    }

    private sealed class NoOpGateway : IMcpGateway
    {
        public Task<McpGatewayIndexBuildResult> BuildIndexAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<McpGatewayToolDescriptor>>([]);

        public Task<McpGatewaySearchResult> SearchAsync(
            string? query,
            int? maxResults = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewaySearchResult> SearchAsync(
            McpGatewaySearchRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayToolRouteResult> RouteToolsAsync(
            McpGatewayToolRouteRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayInvokeResult> InvokeAsync(
            McpGatewayInvokeRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IReadOnlyList<AITool> CreateMetaTools(
            string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
            string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
            string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
        ) => [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpPromptCatalog : IMcpGatewayPromptCatalog
    {
        public Task<IReadOnlyList<McpGatewayPromptDescriptor>> ListPromptsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<McpGatewayPromptDescriptor>>([]);

        public Task<McpGatewayPromptResult?> GetPromptAsync(
            McpGatewayPromptRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class StaticResourceCatalog(
        IReadOnlyList<McpGatewayResourceDescriptor> resources,
        IReadOnlyList<McpGatewayResourceTemplateDescriptor> templates
    ) : IMcpGatewayResourceCatalog
    {
        public Task<IReadOnlyList<McpGatewayResourceDescriptor>> ListResourcesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(resources);

        public Task<IReadOnlyList<McpGatewayResourceTemplateDescriptor>> ListResourceTemplatesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(templates);

        public Task<McpGatewayResourceResult?> ReadResourceAsync(
            McpGatewayResourceRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class NoOpRegistry : IMcpGatewayRegistry
    {
        public void AddTool(string sourceId, AITool tool, string? displayName = null) =>
            throw new NotSupportedException();

        public void AddTool(
            string sourceId,
            AITool tool,
            McpGatewayToolSearchHints searchHints,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddTool(AITool tool, string sourceId = "local", string? displayName = null) =>
            throw new NotSupportedException();

        public void AddTool(
            AITool tool,
            McpGatewayToolSearchHints searchHints,
            string sourceId = "local",
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddTools(
            string sourceId,
            IEnumerable<AITool> tools,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddTools(
            IEnumerable<AITool> tools,
            string sourceId = "local",
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddPrompt(
            string sourceId,
            McpGatewayPrompt prompt,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddPrompt(
            McpGatewayPrompt prompt,
            string sourceId = "local",
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddPrompts(
            string sourceId,
            IEnumerable<McpGatewayPrompt> prompts,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddPrompts(
            IEnumerable<McpGatewayPrompt> prompts,
            string sourceId = "local",
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddHttpServer(
            string sourceId,
            Uri endpoint,
            IReadOnlyDictionary<string, string>? headers = null,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddHttpServer(McpGatewayHttpServerOptions httpServer) =>
            throw new NotSupportedException();

        public void AddStdioServer(
            string sourceId,
            string command,
            IReadOnlyList<string>? arguments = null,
            string? workingDirectory = null,
            IReadOnlyDictionary<string, string?>? environmentVariables = null,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddStdioServer(McpGatewayStdioServerOptions stdioServer) =>
            throw new NotSupportedException();

        public void AddMcpClient(
            string sourceId,
            ModelContextProtocol.Client.McpClient client,
            bool disposeClient = false,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddMcpClientFactory(
            string sourceId,
            Func<CancellationToken, ValueTask<ModelContextProtocol.Client.McpClient>> clientFactory,
            bool disposeClient = true,
            string? displayName = null
        ) => throw new NotSupportedException();
    }
}

#pragma warning restore MCPEXP001
