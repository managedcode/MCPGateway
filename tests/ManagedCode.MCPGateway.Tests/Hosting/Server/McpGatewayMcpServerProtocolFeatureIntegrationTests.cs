using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerProtocolFeatureIntegrationTests
{
    [Test]
    public async Task WithMcpGatewayCatalog_UsesCustomBindingResolverForIsolatedGatewayInstance()
    {
        await using var upstreamServer = await TestMcpServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
                    new IsolatedGatewayBindingResolver(
                        serviceProvider.GetRequiredService<IMcpGatewayFactory>(),
                        upstreamServer.Client
                    )
                )
        );

        var tools = await gatewayServer.Client.ListToolsAsync();
        var toolResult = await gatewayServer.Client.CallToolAsync(
            "isolated:plain_text_search",
            new Dictionary<string, object?> { ["query"] = "mcp" }
        );
        var prompts = await gatewayServer.Client.ListPromptsAsync();
        var prompt = await gatewayServer.Client.GetPromptAsync(
            "isolated:repository_triage_system_prompt",
            new Dictionary<string, object?>
            {
                ["repository"] = "ManagedCode/MCPGateway",
                ["locale"] = "uk-UA",
            }
        );
        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == "isolated:repository_overview"
        );
        var resourceResult = await gatewayServer.Client.ReadResourceAsync(resource.Uri);

        await Assert.That(tools.Any(static tool => tool.Name == "isolated:plain_text_search")).IsTrue();
        await Assert.That(GetSingleText(toolResult)).IsEqualTo("plain:mcp");
        await Assert
            .That(
                prompts.Any(static prompt =>
                    prompt.Name == "isolated:repository_triage_system_prompt"
                )
            )
            .IsTrue();
        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(prompt.Messages[0].Content).IsTypeOf<TextContentBlock>();
        await Assert
            .That(((TextContentBlock)prompt.Messages[0].Content).Text)
            .Contains("ManagedCode/MCPGateway");
        await Assert.That(resourceResult.Contents.Count).IsEqualTo(1);
        await Assert.That(resourceResult.Contents[0]).IsTypeOf<TextResourceContents>();
        await Assert
            .That(((TextResourceContents)resourceResult.Contents[0]).Text)
            .Contains("aggregates MCP tools");
    }

    [Test]
    public async Task CompleteAsync_CompletesPromptArgumentFromAggregatedUpstreamServer()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var completion = await gatewayServer.Client.CompleteAsync(
            new PromptReference { Name = $"source-a:{TestMcpProtocolFeatureServerHost.PromptName}" },
            TestMcpProtocolFeatureServerHost.PromptArgumentName,
            "Managed"
        );

        await Assert.That(gatewayServer.Client.ServerCapabilities.Completions).IsNotNull();
        await Assert.That(completion.Completion.Values).IsEquivalentTo(
            ["ManagedCode/MCPGateway", "ManagedCode/AIBase"]
        );
        await Assert.That(completion.Completion.Total).IsEqualTo(2);
    }

    [Test]
    public async Task CompleteAsync_CompletesResourceTemplateArgumentFromAggregatedUpstreamServer()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var template = (await gatewayServer.Client.ListResourceTemplatesAsync()).Single(
            static candidate =>
                candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceTemplateName}"
        );
        var completion = await gatewayServer.Client.CompleteAsync(
            new ResourceTemplateReference { Uri = template.UriTemplate },
            TestMcpProtocolFeatureServerHost.ResourceTemplateArgumentName,
            "model"
        );

        await Assert.That(completion.Completion.Values).IsEquivalentTo(["modelcontextprotocol"]);
        await Assert.That(completion.Completion.Total).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeToResourceAsync_ForwardsUpstreamResourceUpdatedNotification()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var updatedResource = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<ResourceUpdatedNotificationParams>();
                if (payload is not null)
                {
                    updatedResource.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceName}"
        );

        await gatewayServer.Client.SubscribeToResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();

        var payload = await updatedResource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(gatewayServer.Client.ServerCapabilities.Resources?.Subscribe).IsTrue();
        await Assert.That(payload.Uri).IsEqualTo(resource.Uri);
    }

    [Test]
    public async Task UnsubscribeFromResourceAsync_StopsForwardingUpstreamNotifications()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var notificationCount = 0;
        var firstNotification = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondNotification = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<ResourceUpdatedNotificationParams>();
                if (payload is null)
                {
                    return ValueTask.CompletedTask;
                }

                notificationCount++;
                if (notificationCount == 1)
                {
                    firstNotification.TrySetResult(payload);
                }
                else
                {
                    secondNotification.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceName}"
        );

        await gatewayServer.Client.SubscribeToResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();
        _ = await firstNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await gatewayServer.Client.UnsubscribeFromResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();

        var completedTask = await Task.WhenAny(
            secondNotification.Task,
            Task.Delay(TimeSpan.FromMilliseconds(300))
        );

        await Assert.That(ReferenceEquals(completedTask, secondNotification.Task)).IsFalse();
        await Assert.That(notificationCount).IsEqualTo(1);
    }

    [Test]
    public async Task SetLoggingLevelAsync_AdvertisesLoggingCapabilityAndUpdatesServerLevel()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });

        await gatewayServer.Client.SetLoggingLevelAsync(ModelContextProtocol.Protocol.LoggingLevel.Debug);

        await Assert.That(gatewayServer.Client.ServerCapabilities.Logging).IsNotNull();
        await Assert.That(gatewayServer.Server.LoggingLevel).IsEqualTo(
            ModelContextProtocol.Protocol.LoggingLevel.Debug
        );
    }

    private static string GetSingleText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static IReadOnlyList<IMcpGatewayServerSource> CreateSources(IMcpGatewayRegistry registry) =>
        ((IMcpGatewayCatalogSource)registry)
            .CreateSnapshot()
            .Registrations.Select(static registration =>
                (IMcpGatewayServerSource)new McpGatewayRegistrationBoundServerSource(registration)
            )
            .ToList();

    private sealed class IsolatedGatewayBindingResolver(
        IMcpGatewayFactory gatewayFactory,
        ModelContextProtocol.Client.McpClient upstreamClient
    ) : IMcpGatewayServerBindingResolver
    {
        public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gatewayInstance = gatewayFactory.Create(options =>
            {
                options.AddMcpClient("isolated", upstreamClient, disposeClient: false);
            });
            var sources = CreateSources(gatewayInstance.Registry);

            return ValueTask.FromResult<IMcpGatewayServerBinding>(
                new McpGatewayServerBinding(
                    gatewayInstance.Gateway,
                    gatewayInstance.PromptCatalog,
                    gatewayInstance.ResourceCatalog,
                    new RegistryFacade(gatewayInstance.Registry),
                    listSourcesAsync: _ => ValueTask.FromResult(sources),
                    disposeAsync: gatewayInstance.DisposeAsync
                )
            );
        }
    }

    private sealed class RegistryFacade(IMcpGatewayRegistry inner) : IMcpGatewayRegistry
    {
        public void AddTool(string sourceId, Microsoft.Extensions.AI.AITool tool, string? displayName = null) =>
            inner.AddTool(sourceId, tool, displayName);

        public void AddTool(
            string sourceId,
            Microsoft.Extensions.AI.AITool tool,
            McpGatewayToolSearchHints searchHints,
            string? displayName = null
        ) => inner.AddTool(sourceId, tool, searchHints, displayName);

        public void AddTool(
            Microsoft.Extensions.AI.AITool tool,
            string sourceId = "local",
            string? displayName = null
        ) => inner.AddTool(tool, sourceId, displayName);

        public void AddTool(
            Microsoft.Extensions.AI.AITool tool,
            McpGatewayToolSearchHints searchHints,
            string sourceId = "local",
            string? displayName = null
        ) => inner.AddTool(tool, searchHints, sourceId, displayName);

        public void AddTools(
            string sourceId,
            IEnumerable<Microsoft.Extensions.AI.AITool> tools,
            string? displayName = null
        ) => inner.AddTools(sourceId, tools, displayName);

        public void AddTools(
            IEnumerable<Microsoft.Extensions.AI.AITool> tools,
            string sourceId = "local",
            string? displayName = null
        ) => inner.AddTools(tools, sourceId, displayName);

        public void AddPrompt(
            string sourceId,
            McpGatewayPrompt prompt,
            string? displayName = null
        ) => inner.AddPrompt(sourceId, prompt, displayName);

        public void AddPrompt(
            McpGatewayPrompt prompt,
            string sourceId = "local",
            string? displayName = null
        ) => inner.AddPrompt(prompt, sourceId, displayName);

        public void AddPrompts(
            string sourceId,
            IEnumerable<McpGatewayPrompt> prompts,
            string? displayName = null
        ) => inner.AddPrompts(sourceId, prompts, displayName);

        public void AddPrompts(
            IEnumerable<McpGatewayPrompt> prompts,
            string sourceId = "local",
            string? displayName = null
        ) => inner.AddPrompts(prompts, sourceId, displayName);

        public void AddHttpServer(
            string sourceId,
            Uri endpoint,
            IReadOnlyDictionary<string, string>? headers = null,
            string? displayName = null
        ) => inner.AddHttpServer(sourceId, endpoint, headers, displayName);

        public void AddStdioServer(
            string sourceId,
            string command,
            IReadOnlyList<string>? arguments = null,
            string? workingDirectory = null,
            IReadOnlyDictionary<string, string?>? environmentVariables = null,
            string? displayName = null
        ) =>
            inner.AddStdioServer(
                sourceId,
                command,
                arguments,
                workingDirectory,
                environmentVariables,
                displayName
            );

        public void AddMcpClient(
            string sourceId,
            ModelContextProtocol.Client.McpClient client,
            bool disposeClient = false,
            string? displayName = null
        ) => inner.AddMcpClient(sourceId, client, disposeClient, displayName);

        public void AddMcpClientFactory(
            string sourceId,
            Func<CancellationToken, ValueTask<ModelContextProtocol.Client.McpClient>> clientFactory,
            bool disposeClient = true,
            string? displayName = null
        ) => inner.AddMcpClientFactory(sourceId, clientFactory, disposeClient, displayName);
    }
}
