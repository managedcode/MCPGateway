using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceSubscriptionManagerTests
{
    [Test]
    public async Task SubscribeAsync_ReplacesExistingSubscriptionAndForwardsNotifications()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var registration = new TrackingResourceRegistration("source-a");
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(serviceProvider, registration);
        var notificationReceived = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var handler = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params is null
                    ? null
                    : JsonSerializer.Deserialize<ResourceUpdatedNotificationParams>(
                        notification.Params.ToJsonString(),
                        McpGatewayJsonSerializer.Options
                    );
                if (payload is not null)
                {
                    notificationReceived.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var exposedUri = McpGatewayResourceUriCodec.ToGatewayUri("source-a", "docs://overview");

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            exposedUri,
            CancellationToken.None
        );
        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            exposedUri,
            CancellationToken.None
        );
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams
            {
                Uri = "docs://overview",
                Meta = new System.Text.Json.Nodes.JsonObject { ["region"] = "eu" },
            },
            CancellationToken.None
        );
        var payload = await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.UnsubscribeAsync(gatewayServer.Server, exposedUri, CancellationToken.None);
        await manager.DisposeAsync();

        await Assert.That(registration.SubscriptionCount).IsEqualTo(2);
        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(2);
        await Assert.That(payload.Uri).IsEqualTo(exposedUri);
    }

    [Test]
    public async Task SubscribeAsync_RemovesSubscriptionWhenForwardingThrows()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var registration = new TrackingResourceRegistration("source-a");
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(serviceProvider, registration);
        using var cancellationSource = new CancellationTokenSource();

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );
        cancellationSource.Cancel();
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams { Uri = "docs://overview" },
            cancellationSource.Token
        );

        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(1);
    }

    private static McpGatewayResourceSubscriptionManager CreateManager(
        ServiceProvider serviceProvider,
        TrackingResourceRegistration registration
    )
    {
        var resolver = new StaticBindingResolver(
            new McpGatewayServerBinding(
                new NoOpGateway(),
                new NoOpPromptCatalog(),
                new StaticResourceCatalog(
                    [
                        new McpGatewayResourceDescriptor(
                            "source-a",
                            McpGatewaySourceKind.Local,
                            "overview",
                            "overview",
                            "docs://overview",
                            "Reads overview.",
                            "text/plain",
                            null
                        ),
                    ],
                    []
                ),
                new StaticRegistry([registration])
            )
        );

        return new McpGatewayResourceSubscriptionManager(
            new McpGatewayMcpServerBindingManager(resolver),
            serviceProvider,
            NullLogger<McpGatewayResourceSubscriptionManager>.Instance,
            NullLoggerFactory.Instance
        );
    }

    private sealed class TrackingResourceRegistration(string sourceId)
        : McpGatewayToolSourceRegistration(sourceId, null)
    {
        private Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>? _onUpdated;

        public int SubscriptionCount { get; private set; }

        public int DisposedSubscriptionCount { get; private set; }

        public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

        public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>([]);

        public override Task<IAsyncDisposable?> SubscribeToResourceAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        )
        {
            _onUpdated = onUpdated;
            SubscriptionCount++;
            return Task.FromResult<IAsyncDisposable?>(new TrackingSubscription(this));
        }

        public ValueTask EmitAsync(
            ResourceUpdatedNotificationParams notification,
            CancellationToken cancellationToken
        ) =>
            _onUpdated is null
                ? ValueTask.CompletedTask
                : _onUpdated(notification, cancellationToken);

        private sealed class TrackingSubscription(TrackingResourceRegistration owner)
            : IAsyncDisposable
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
        public Task<McpGatewayIndexBuildResult> BuildIndexAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

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

    private sealed class StaticRegistry(IReadOnlyList<McpGatewayToolSourceRegistration> registrations)
        : IMcpGatewayRegistry, IMcpGatewayCatalogSource
    {
        public McpGatewayCatalogSourceSnapshot CreateSnapshot() => new(1, registrations);

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

        public void AddHttpServer(
            string sourceId,
            Uri endpoint,
            ModelContextProtocol.Client.HttpTransportMode transportMode,
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
