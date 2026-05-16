#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceSubscriptionManagerConcurrencyTests
{
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
            new NoOpRegistry(),
            listSourcesAsync: _ => ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>([source]),
            disposeAsync: () =>
            {
                bindingDisposeCount++;
                return ValueTask.CompletedTask;
            }
        );
        var manager = new McpGatewayResourceSubscriptionManager(
            new McpGatewayMcpServerBindingManager(new StaticBindingResolver(binding)),
            serviceProvider,
            NullLogger<McpGatewayResourceSubscriptionManager>.Instance,
            NullLoggerFactory.Instance
        );

        var firstSubscribe = manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );
        await source.FirstSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondSubscribe = manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );

        source.ReleaseFirstSubscription.SetResult(true);

        await firstSubscribe.WaitAsync(TimeSpan.FromSeconds(5));
        await secondSubscribe.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.UnsubscribeAsync(gatewayServer.Server, "docs://overview", CancellationToken.None);

        await Assert.That(source.SubscriptionCount).IsEqualTo(2);
        await Assert.That(source.DisposedSubscriptionCount).IsEqualTo(2);
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
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

        public ValueTask<ToolTaskSupport?> GetToolTaskSupportAsync(
            string toolName,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<ToolTaskSupport?>(null);

        public ValueTask<CompleteResult?> CompleteAsync(
            Reference reference,
            Argument argument,
            CompleteContext? context,
            IServiceProvider? serviceProvider,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<CompleteResult?>(null);

        public Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
            Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask<McpTask?> CallToolAsTaskAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            McpTaskMetadata taskMetadata,
            IProgress<ModelContextProtocol.ProgressNotificationValue>? progress,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<McpTask?>(null);

        public ValueTask<McpTask?> GetTaskAsync(
            string taskId,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<McpTask?>(null);

        public ValueTask<System.Text.Json.JsonElement?> GetTaskResultAsync(
            string taskId,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<System.Text.Json.JsonElement?>(null);

        public ValueTask<McpTask?> CancelTaskAsync(
            string taskId,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<McpTask?>(null);

        public async Task<IAsyncDisposable?> SubscribeToResourceAsync(
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

        public Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
            string taskId,
            Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IAsyncDisposable?>(null);

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

#pragma warning restore MCPEXP001
