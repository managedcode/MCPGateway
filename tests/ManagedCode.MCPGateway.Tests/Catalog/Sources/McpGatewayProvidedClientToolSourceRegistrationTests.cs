#pragma warning disable MCPEXP001

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProvidedClientToolSourceRegistrationTests
{
    private const string RejectedProtocolVersion = "2025-11-25";

    [Test]
    public async Task PromptResourceAndCompletionMethods_ForwardToProvidedClient()
    {
        await using var contentServer = await TestMcpServerHost.StartAsync();
        await using var protocolServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        var protocolRegistration = new McpGatewayProvidedClientToolSourceRegistration(
            "protocol",
            _ => ValueTask.FromResult(protocolServer.Client),
            disposeClient: false,
            displayName: null
        );
        var promptListRegistration = new McpGatewayProvidedClientToolSourceRegistration(
            "content",
            _ => ValueTask.FromResult(contentServer.Client),
            disposeClient: false,
            displayName: null
        );

        var contentTools = await promptListRegistration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var prompts = await promptListRegistration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resources = await promptListRegistration.LoadResourcesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var templates = await promptListRegistration.LoadResourceTemplatesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var prompt = await promptListRegistration.GetPromptAsync(
            "repository_triage_system_prompt",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["repository"] = "ManagedCode/MCPGateway",
            },
            promptContext: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resource = await promptListRegistration.ReadResourceAsync(
            "docs://repository/overview",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var completion = await protocolRegistration.CompleteAsync(
            new PromptReference { Name = TestMcpProtocolFeatureServerHost.PromptName },
            new Argument
            {
                Name = TestMcpProtocolFeatureServerHost.PromptArgumentName,
                Value = "Managed",
            },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(contentTools.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(prompts.Count).IsEqualTo(2);
        await Assert.That(resources.Count).IsEqualTo(2);
        await Assert.That(templates.Count).IsEqualTo(1);
        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Messages.Count).IsEqualTo(1);
        await Assert.That(resource).IsNotNull();
        await Assert.That(resource!.Contents.Count).IsEqualTo(1);
        await Assert.That(completion).IsNotNull();
        await Assert.That(completion!.Completion.Values).Contains("ManagedCode/MCPGateway");

    }

    [Test]
    public async Task MissingCapabilities_ReturnNullOrEmptyWithoutThrowing()
    {
        await using var taskServer = await TestMcpTaskFeatureServerHost.StartAsync();
        var registration = new McpGatewayProvidedClientToolSourceRegistration(
            "tasks",
            _ => ValueTask.FromResult(taskServer.Client),
            disposeClient: false,
            displayName: null
        );

        var prompts = await registration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resources = await registration.LoadResourcesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var templates = await registration.LoadResourceTemplatesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var prompt = await registration.GetPromptAsync(
            "missing",
            arguments: null,
            promptContext: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resource = await registration.ReadResourceAsync(
            "docs://missing",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var completion = await registration.CompleteAsync(
            new PromptReference { Name = "missing" },
            new Argument { Name = "value", Value = "a" },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resourceSubscription = await registration.ListenForResourceUpdatesAsync(
            "docs://missing",
            static (_, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(prompts.Count).IsEqualTo(0);
        await Assert.That(resources.Count).IsEqualTo(0);
        await Assert.That(templates.Count).IsEqualTo(0);
        await Assert.That(prompt).IsNull();
        await Assert.That(resource).IsNull();
        await Assert.That(completion).IsNull();
        await Assert.That(resourceSubscription).IsNull();
    }

    [Test]
    public async Task CancelledClientCreation_CachesCreatedClientSoDisposeReleasesIt()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        var factoryStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFactory = new TaskCompletionSource<McpClient>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factoryReturned = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var registration = new McpGatewayProvidedClientToolSourceRegistration(
            "delayed",
            async _ =>
            {
                factoryStarted.TrySetResult(null);
                var client = await releaseFactory.Task;
                factoryReturned.TrySetResult(null);
                return client;
            },
            disposeClient: true,
            displayName: null
        );
        using var cancellationSource = new CancellationTokenSource();

        var loadTask = registration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            cancellationSource.Token
        );
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        Exception? loadException = null;
        try
        {
            _ = await loadTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            loadException = exception;
        }

        releaseFactory.TrySetResult(serverHost.Client);
        await factoryReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCachedClientAsync(registration);

        await registration.DisposeAsync();

        await Assert.That(loadException).IsTypeOf<OperationCanceledException>();
        await Assert.That(registration.HasCachedClient).IsFalse();
    }

    [Test]
    public async Task MissingPromptListCapability_ReturnsNullSubscription()
    {
        await using var taskServer = await TestMcpTaskFeatureServerHost.StartAsync();
        var promptRegistration = new McpGatewayProvidedClientToolSourceRegistration(
            "promptless",
            _ => ValueTask.FromResult(taskServer.Client),
            disposeClient: false,
            displayName: null
        );
        var promptSubscription = await promptRegistration.ListenForPromptListChangesAsync(
            static (_, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        await Assert.That(promptSubscription).IsNull();
    }

    [Test]
    public async Task PromptAndResourceSubscriptions_AreForwardedWhenCapabilitiesExist()
    {
        await using var promptServer = await TestMcpPromptListFeatureServerHost.StartAsync();
        await using var protocolServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        var promptRegistration = new McpGatewayProvidedClientToolSourceRegistration(
            "prompt-source",
            _ => ValueTask.FromResult(promptServer.Client),
            disposeClient: false,
            displayName: null
        );
        var resourceRegistration = new McpGatewayProvidedClientToolSourceRegistration(
            "resource-source",
            _ => ValueTask.FromResult(protocolServer.Client),
            disposeClient: false,
            displayName: null
        );
        var promptChanged = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resourceUpdated = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var promptSubscriptionWithSignal =
            await promptRegistration.ListenForPromptListChangesAsync(
                (_, _) =>
                {
                    promptChanged.TrySetResult(true);
                    return ValueTask.CompletedTask;
                },
                NullLoggerFactory.Instance,
                CancellationToken.None
            );
        await using var resourceSubscription = await resourceRegistration.ListenForResourceUpdatesAsync(
            TestMcpProtocolFeatureServerHost.ResourceUri,
            (notification, _) =>
            {
                resourceUpdated.TrySetResult(notification);
                return ValueTask.CompletedTask;
            },
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await promptServer.AddPromptAsync("fresh_prompt");
        await protocolServer.EmitResourceUpdatedAsync();

        await Assert.That(await promptChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert
            .That((await resourceUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5))).Uri)
            .IsEqualTo(TestMcpProtocolFeatureServerHost.ResourceUri);
        await Assert.That(promptSubscriptionWithSignal).IsNotNull();
        await Assert.That(resourceSubscription).IsNotNull();
    }

    [Test]
    public async Task LoadToolsAsync_PreservesTheOwningClientForTaskAwareInvocation()
    {
        await using var taskServer = await TestMcpTaskFeatureServerHost.StartAsync();
        var registration = new McpGatewayProvidedClientToolSourceRegistration(
            "task-source",
            _ => ValueTask.FromResult(taskServer.Client),
            disposeClient: false,
            displayName: null
        );
        var tools = await registration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(tools.Count).IsEqualTo(3);
        await Assert.That(tools.All(tool => ReferenceEquals(tool.Client, taskServer.Client))).IsTrue();
    }

    [Test]
    public async Task LoadToolsAsync_RejectsProvidedClientUsingNonCurrentProtocol()
    {
        await using var serverHost = await TestMcpServerHost.StartWithProtocolVersionAsync(
            RejectedProtocolVersion
        );
        var registration = new McpGatewayProvidedClientToolSourceRegistration(
            "rejected-protocol",
            _ => ValueTask.FromResult(serverHost.Client),
            disposeClient: false,
            displayName: null
        );

        var exception = await CaptureAsync(
            registration
                .LoadToolsAsync(NullLoggerFactory.Instance, CancellationToken.None)
                .AsTask()
        );

        await Assert.That(exception).IsTypeOf<UnsupportedProtocolVersionException>();
        var protocolException = (UnsupportedProtocolVersionException)exception!;
        await Assert.That(protocolException.Requested).IsEqualTo(RejectedProtocolVersion);
        await Assert
            .That(protocolException.Supported)
            .IsEquivalentTo([McpGatewayMcpProtocolConstants.CurrentProtocolVersion]);
    }

    private static async Task<Exception?> CaptureAsync(Task action)
    {
        try
        {
            await action;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitForCachedClientAsync(
        McpGatewayProvidedClientToolSourceRegistration registration
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (registration.HasCachedClient)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        await Assert.That(registration.HasCachedClient).IsTrue();
    }
}

#pragma warning restore MCPEXP001
