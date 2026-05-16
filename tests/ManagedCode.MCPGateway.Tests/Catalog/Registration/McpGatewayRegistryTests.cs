using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayRegistryTests
{
    [Test]
    public async Task Mutations_AdvanceSnapshotVersionAndNotifyPromptChangeHubOnlyWhenRelevant()
    {
        var promptChangeHub = new McpGatewayPromptChangeHub();
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            promptChangeHub
        );
        var notificationCount = 0;
        using var subscription = promptChangeHub.Subscribe(() => notificationCount++);

        registry.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "ok",
                "local_lookup",
                "Looks up a value."
            )
        );
        var afterToolSnapshot = registry.CreateSnapshot();

        registry.AddPrompt(
            new McpGatewayPrompt("release_review", BuildPromptAsync)
            {
                Description = "Builds a release review prompt.",
            }
        );
        registry.AddHttpServer("http-source", new Uri("https://example.com/mcp"));
        await registry.ClearAsync();

        var finalSnapshot = registry.CreateSnapshot();

        await Assert.That(afterToolSnapshot.Version).IsEqualTo(1);
        await Assert.That(finalSnapshot.Version).IsEqualTo(4);
        await Assert.That(notificationCount).IsEqualTo(3);
        await Assert.That(finalSnapshot.Registrations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReconfigureAsync_ReplacesRegistrationsAndRefreshesSnapshot()
    {
        var promptChangeHub = new McpGatewayPromptChangeHub();
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            promptChangeHub
        );

        registry.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "old",
                "old_tool",
                "Returns old data."
            )
        );

        var replacement = new McpGatewayOptions().AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "new",
                "new_tool",
                "Returns new data."
            )
        );

        await registry.ReconfigureAsync(replacement);

        var registrations = registry.CreateSnapshot().Registrations;
        var localRegistration = registrations.OfType<McpGatewayLocalToolSourceRegistration>().Single();
        var tools = await localRegistration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(registrations.Count).IsEqualTo(1);
        await Assert.That(tools.Count).IsEqualTo(1);
        await Assert.That(tools[0].Tool.Name).IsEqualTo("new_tool");
    }

    [Test]
    public async Task ReconfigureAsync_DisposesAllPreviousRegistrationsWhenOneDisposeFails()
    {
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            new McpGatewayPromptChangeHub()
        );
        var failingRegistration = new TrackingRegistration("failing", throwOnDispose: true);
        var secondRegistration = new TrackingRegistration("second");
        ReplaceRegistrations(registry, failingRegistration, secondRegistration);

        Exception? exception = null;
        try
        {
            await registry.ReconfigureAsync(new McpGatewayOptions());
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(failingRegistration.DisposeCount).IsEqualTo(1);
        await Assert.That(secondRegistration.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_IsIdempotentAndRejectsFurtherMutations()
    {
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            new McpGatewayPromptChangeHub()
        );

        registry.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "ok",
                "local_lookup",
                "Looks up a value."
            )
        );

        await registry.DisposeAsync();
        await registry.DisposeAsync();

        ObjectDisposedException? exception = null;
        try
        {
            registry.AddTool(
                TestFunctionFactory.CreateFunction(
                    static () => "later",
                    "late_tool",
                    "Should not be added."
                )
            );
        }
        catch (ObjectDisposedException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task DisposeAsync_DisposesAllRegistrationsWhenOneDisposeFails()
    {
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            new McpGatewayPromptChangeHub()
        );
        var failingRegistration = new TrackingRegistration("failing", throwOnDispose: true);
        var secondRegistration = new TrackingRegistration("second");
        ReplaceRegistrations(registry, failingRegistration, secondRegistration);

        Exception? exception = null;
        try
        {
            await registry.DisposeAsync();
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(failingRegistration.DisposeCount).IsEqualTo(1);
        await Assert.That(secondRegistration.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task OverloadMethods_RegisterAllSupportedMutationShapes()
    {
        await using var upstreamServer = await TestMcpServerHost.StartAsync();
        var promptChangeHub = new McpGatewayPromptChangeHub();
        var registry = new McpGatewayRegistry(
            Options.Create(new McpGatewayOptions()),
            promptChangeHub
        );
        var notificationCount = 0;
        using var subscription = promptChangeHub.Subscribe(() => notificationCount++);
        var searchHints = new McpGatewayToolSearchHints(["lookup"], ["ops"]);

        registry.AddTool(
            "alpha",
            TestFunctionFactory.CreateFunction(static () => "alpha", "alpha_tool", "Returns alpha."),
            searchHints
        );
        registry.AddTool(
            TestFunctionFactory.CreateFunction(static () => "beta", "beta_tool", "Returns beta."),
            searchHints,
            "beta"
        );
        registry.AddTools(
            "gamma",
            [
                TestFunctionFactory.CreateFunction(
                    static () => "gamma-a",
                    "gamma_a",
                    "Returns gamma-a."
                ),
            ]
        );
        registry.AddTools(
            [
                TestFunctionFactory.CreateFunction(
                    static () => "delta-a",
                    "delta_a",
                    "Returns delta-a."
                ),
            ],
            "delta"
        );
        registry.AddPrompts(
            "epsilon",
            [new McpGatewayPrompt("epsilon_prompt", BuildPromptAsync)]
        );
        registry.AddPrompts([new McpGatewayPrompt("zeta_prompt", BuildPromptAsync)], "zeta");
        registry.AddStdioServer("stdio-source", "dotnet", ["run"]);
        registry.AddMcpClient("provided-client", upstreamServer.Client, disposeClient: false);
        registry.AddMcpClientFactory(
            "factory-client",
            _ => ValueTask.FromResult(upstreamServer.Client),
            disposeClient: false
        );

        var registrations = registry.CreateSnapshot().Registrations;
        var localRegistrations = registrations.OfType<McpGatewayLocalToolSourceRegistration>().ToArray();
        var loadedTools = await Task.WhenAll(
            localRegistrations.Select(registration =>
                registration.LoadToolsAsync(NullLoggerFactory.Instance, CancellationToken.None).AsTask()
            )
        );
        var loadedPrompts = await Task.WhenAll(
            localRegistrations.Select(registration =>
                registration.LoadPromptsAsync(NullLoggerFactory.Instance, CancellationToken.None).AsTask()
            )
        );

        await Assert.That(registrations.Count).IsEqualTo(9);
        await Assert.That(loadedTools.Sum(static tools => tools.Count)).IsEqualTo(4);
        await Assert.That(loadedPrompts.Sum(static prompts => prompts.Count)).IsEqualTo(2);
        await Assert.That(notificationCount).IsEqualTo(5);
    }

    private static ValueTask<ModelContextProtocol.Protocol.GetPromptResult> BuildPromptAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            new ModelContextProtocol.Protocol.GetPromptResult
            {
                Messages =
                [
                    new ModelContextProtocol.Protocol.PromptMessage
                    {
                        Role = ModelContextProtocol.Protocol.Role.User,
                        Content = new ModelContextProtocol.Protocol.TextContentBlock
                        {
                            Text = context.PromptName,
                        },
                    },
                ],
            }
        );
    }

    private static void ReplaceRegistrations(
        McpGatewayRegistry registry,
        params McpGatewayToolSourceRegistration[] registrations
    )
    {
        typeof(McpGatewayRegistry)
            .GetField("_registrations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(registry, new McpGatewayRegistrationCollection(registrations));
    }

    private sealed class TrackingRegistration(string sourceId, bool throwOnDispose = false)
        : McpGatewayToolSourceRegistration(sourceId, displayName: null)
    {
        public int DisposeCount { get; private set; }

        public override McpGatewaySourceRegistrationKind Kind =>
            McpGatewaySourceRegistrationKind.Local;

        public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>([]);

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            if (throwOnDispose)
            {
                throw new InvalidOperationException($"Dispose failed for '{SourceId}'.");
            }
        }
    }
}
