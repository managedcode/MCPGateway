#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayMcpServerRequestResolverTests
{
    [Test]
    public async Task ResolveToolAsync_HandlesBlankUnknownAndAmbiguousNames()
    {
        var alphaRegistration = new TestRegistration(
            "alpha",
            [CreateLoadedTool("shared_tool", ToolTaskSupport.Optional)]
        );
        var betaRegistration = new TestRegistration(
            "beta",
            [CreateLoadedTool("shared_tool", ToolTaskSupport.Required)]
        );
        var (resolver, binding) = CreateResolverContext(
            toolDescriptors:
            [
                CreateToolDescriptor("alpha:shared_tool", "alpha", "shared_tool"),
                CreateToolDescriptor("beta:shared_tool", "beta", "shared_tool"),
            ],
            registrations: [alphaRegistration, betaRegistration]
        );

        var blank = await resolver.ResolveToolAsync(binding, " ", CancellationToken.None);
        var unknown = await resolver.ResolveToolAsync(binding, "missing", CancellationToken.None);
        var resolved = await resolver.ResolveToolAsync(
            binding,
            "alpha:shared_tool",
            CancellationToken.None
        );
        var exception = await CaptureAsync(() =>
            resolver.ResolveToolAsync(binding, "shared_tool", CancellationToken.None)
        );

        await Assert.That(blank).IsNull();
        await Assert.That(unknown).IsNull();
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.ToolId).IsEqualTo("alpha:shared_tool");
        await Assert.That(resolved.TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("ambiguous");
    }

    [Test]
    public async Task LoadToolTaskSupportsAsync_SkipsMissingRegistrationsAndMissingTools()
    {
        var alphaRegistration = new TestRegistration(
            "alpha",
            [CreateLoadedTool("known_tool", ToolTaskSupport.Required)]
        );
        var (resolver, binding) = CreateResolverContext(
            toolDescriptors:
            [
                CreateToolDescriptor("alpha:known_tool", "alpha", "known_tool"),
                CreateToolDescriptor("alpha:missing_tool", "alpha", "missing_tool"),
                CreateToolDescriptor("beta:other_tool", "beta", "other_tool"),
            ],
            registrations: [alphaRegistration]
        );

        var supports = await resolver.LoadToolTaskSupportsAsync(binding, CancellationToken.None);

        await Assert.That(supports.Count).IsEqualTo(2);
        await Assert.That(supports["alpha:known_tool"]).IsEqualTo(ToolTaskSupport.Required);
        await Assert.That(supports["alpha:missing_tool"]).IsNull();
        await Assert
            .That(supports.ContainsKey("beta:other_tool"))
            .IsFalse();
    }

    [Test]
    public async Task ResolvePromptAsync_UsesPromptIdsAndDetectsAmbiguity()
    {
        var alphaRegistration = new TestRegistration("alpha");
        var betaRegistration = new TestRegistration("beta");
        var (resolver, binding) = CreateResolverContext(
            promptDescriptors:
            [
                CreatePromptDescriptor("alpha:release_review", "alpha", "release_review"),
                CreatePromptDescriptor("beta:release_review", "beta", "release_review"),
                CreatePromptDescriptor("gamma:missing", "gamma", "missing"),
            ],
            registrations: [alphaRegistration, betaRegistration]
        );

        var byId = await McpGatewayMcpServerRequestResolver.ResolvePromptAsync(
            binding,
            "alpha:release_review",
            CancellationToken.None
        );
        var blank = await McpGatewayMcpServerRequestResolver.ResolvePromptAsync(
            binding,
            " ",
            CancellationToken.None
        );
        var missingRegistration = await McpGatewayMcpServerRequestResolver.ResolvePromptAsync(
            binding,
            "gamma:missing",
            CancellationToken.None
        );
        var exception = await CaptureAsync(() =>
            McpGatewayMcpServerRequestResolver.ResolvePromptAsync(
                binding,
                "release_review",
                CancellationToken.None
            )
        );

        await Assert.That(byId).IsNotNull();
        await Assert.That(byId!.SourceId).IsEqualTo("alpha");
        await Assert.That(blank).IsNull();
        await Assert.That(missingRegistration).IsNull();
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("ambiguous");
    }

    [Test]
    public async Task ResolveResourceAsync_HandlesGatewayUrisDirectUrisAndAmbiguity()
    {
        var alphaRegistration = new TestRegistration("alpha");
        var betaRegistration = new TestRegistration("beta");
        var gatewayUri = McpGatewayResourceUriCodec.ToGatewayUri("alpha", "docs://overview");
        var (resolver, binding) = CreateResolverContext(
            resourceDescriptors:
            [
                CreateResourceDescriptor("alpha", "overview", "docs://overview"),
                CreateResourceDescriptor("beta", "overview", "docs://overview"),
                CreateResourceDescriptor("gamma", "missing", "docs://missing"),
            ],
            registrations: [alphaRegistration, betaRegistration]
        );

        var gatewayResolved = await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
            binding,
            gatewayUri,
            CancellationToken.None
        );
        var missingGateway = await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
            binding,
            McpGatewayResourceUriCodec.ToGatewayUri("gamma", "docs://missing"),
            CancellationToken.None
        );
        var exception = await CaptureAsync(() =>
            McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
                binding,
                "docs://overview",
                CancellationToken.None
            )
        );

        await Assert.That(gatewayResolved).IsNotNull();
        await Assert.That(gatewayResolved!.UseGatewayUri).IsTrue();
        await Assert.That(gatewayResolved.UpstreamUri).IsEqualTo("docs://overview");
        await Assert.That(missingGateway).IsNull();
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("ambiguous");
    }

    private static (
        McpGatewayMcpServerRequestResolver Resolver,
        IMcpGatewayServerBinding Binding
    ) CreateResolverContext(
        IReadOnlyList<McpGatewayToolDescriptor>? toolDescriptors = null,
        IReadOnlyList<McpGatewayPromptDescriptor>? promptDescriptors = null,
        IReadOnlyList<McpGatewayResourceDescriptor>? resourceDescriptors = null,
        IReadOnlyList<McpGatewayResourceTemplateDescriptor>? templateDescriptors = null,
        IReadOnlyList<McpGatewayToolSourceRegistration>? registrations = null
    ) =>
        (
            new McpGatewayMcpServerRequestResolver(NullLoggerFactory.Instance),
            new McpGatewayServerBinding(
                new TestGateway(toolDescriptors ?? []),
                new TestPromptCatalog(promptDescriptors ?? []),
                new TestResourceCatalog(resourceDescriptors ?? [], templateDescriptors ?? []),
                new TestRegistry(registrations ?? [])
            )
        );

    private static McpGatewayLoadedTool CreateLoadedTool(
        string toolName,
        ToolTaskSupport taskSupport
    ) =>
        new(
            TestFunctionFactory.CreateFunction(
                static () => "ok",
                toolName,
                $"Executes {toolName}."
            ),
            TaskSupport: taskSupport
        );

    private static McpGatewayToolDescriptor CreateToolDescriptor(
        string toolId,
        string sourceId,
        string toolName
    ) =>
        new(
            toolId,
            sourceId,
            McpGatewaySourceKind.Local,
            new Tool
            {
                Name = toolName,
                Title = toolName,
                Description = $"Executes {toolName}.",
                InputSchema = JsonSerializer.SerializeToElement(
                    new { type = "object" },
                    McpGatewayJsonSerializer.Options
                ),
            },
            []
        );

    private static McpGatewayPromptDescriptor CreatePromptDescriptor(
        string promptId,
        string sourceId,
        string promptName
    ) =>
        new(
            promptId,
            sourceId,
            McpGatewaySourceKind.Local,
            promptName,
            promptName,
            $"Builds {promptName}.",
            []
        );

    private static McpGatewayResourceDescriptor CreateResourceDescriptor(
        string sourceId,
        string resourceName,
        string resourceUri
    ) =>
        new(
            sourceId,
            McpGatewaySourceKind.Local,
            resourceName,
            resourceName,
            resourceUri,
            $"Reads {resourceName}.",
            "text/plain",
            null
        );

    private static McpGatewayResourceTemplateDescriptor CreateTemplateDescriptor(
        string sourceId,
        string resourceName,
        string uriTemplate
    ) =>
        new(
            sourceId,
            McpGatewaySourceKind.Local,
            resourceName,
            resourceName,
            uriTemplate,
            $"Reads {resourceName}.",
            "text/plain"
        );

    private static async Task<McpException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (McpException exception)
        {
            return exception;
        }
    }

    private sealed class TestGateway(IReadOnlyList<McpGatewayToolDescriptor> descriptors)
        : IMcpGateway
    {
        public Task<McpGatewayIndexBuildResult> BuildIndexAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(descriptors);

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

    private sealed class TestPromptCatalog(IReadOnlyList<McpGatewayPromptDescriptor> descriptors)
        : IMcpGatewayPromptCatalog
    {
        public Task<IReadOnlyList<McpGatewayPromptDescriptor>> ListPromptsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(descriptors);

        public Task<McpGatewayPromptResult?> GetPromptAsync(
            McpGatewayPromptRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class TestResourceCatalog(
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

    private sealed class TestRegistry(IReadOnlyList<McpGatewayToolSourceRegistration> registrations)
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

    private sealed class TestRegistration(
        string sourceId,
        IReadOnlyList<McpGatewayLoadedTool>? tools = null
    ) : McpGatewayToolSourceRegistration(sourceId, null)
    {
        public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

        public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(tools ?? (IReadOnlyList<McpGatewayLoadedTool>)[]);
    }
}

#pragma warning restore MCPEXP001
