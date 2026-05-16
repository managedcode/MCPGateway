#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class SingleSourceServerBindingResolver(
    IMcpGatewayServerSource source,
    IMcpGatewayResourceCatalog resourceCatalog,
    IMcpGatewayPromptCatalog? promptCatalog = null,
    Action? onDisposed = null
) : IMcpGatewayServerBindingResolver
{
    public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken = default
    )
    {
        _ = requestServices;
        _ = serverServices;
        _ = server;
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IMcpGatewayServerBinding>(
            new Binding(source, resourceCatalog, promptCatalog, onDisposed)
        );
    }

    private sealed class Binding(
        IMcpGatewayServerSource source,
        IMcpGatewayResourceCatalog resourceCatalog,
        IMcpGatewayPromptCatalog? promptCatalog,
        Action? onDisposed
    ) : IMcpGatewayServerBinding
    {
        public IMcpGateway Gateway { get; } = new NoOpGateway();

        public IMcpGatewayPromptCatalog PromptCatalog { get; } =
            promptCatalog ?? new StaticPromptCatalog();

        public IMcpGatewayResourceCatalog ResourceCatalog { get; } = resourceCatalog;

        public IMcpGatewayRegistry Registry { get; } = new NoOpRegistry();

        public IDisposable SubscribeToPromptListChanges(Action onChanged)
        {
            _ = onChanged;
            return NoopDisposable.Instance;
        }

        public ValueTask<IReadOnlyList<IMcpGatewayServerSource>> ListSourcesAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>([source]);
        }

        public ValueTask DisposeAsync()
        {
            onDisposed?.Invoke();
            return ValueTask.CompletedTask;
        }
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
            HttpTransportMode transportMode,
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
            McpClient client,
            bool disposeClient = false,
            string? displayName = null
        ) => throw new NotSupportedException();

        public void AddMcpClientFactory(
            string sourceId,
            Func<CancellationToken, ValueTask<McpClient>> clientFactory,
            bool disposeClient = true,
            string? displayName = null
        ) => throw new NotSupportedException();
    }

    private sealed class StaticPromptCatalog : IMcpGatewayPromptCatalog
    {
        public Task<IReadOnlyList<McpGatewayPromptDescriptor>> ListPromptsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<McpGatewayPromptDescriptor>>([]);

        public Task<McpGatewayPromptResult?> GetPromptAsync(
            McpGatewayPromptRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<McpGatewayPromptResult?>(null);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}

#pragma warning restore MCPEXP001
