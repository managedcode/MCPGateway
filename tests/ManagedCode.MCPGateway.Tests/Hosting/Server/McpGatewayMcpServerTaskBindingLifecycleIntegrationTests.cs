#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskBindingLifecycleIntegrationTests
{
    private const string LocalCancellableTaskToolName = "isolated_local_cancellable_task";

    [Test]
    public async Task CallToolAsTaskAsync_CompletedUpstreamTaskReleasesIsolatedBindingAndKeepsResult()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        var disposeCount = 0;
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
                    new TrackingTaskBindingResolver(
                        serviceProvider.GetRequiredService<IMcpGatewayFactory>(),
                        options =>
                            options.AddMcpClient("isolated", upstreamServer.Client, disposeClient: false),
                        () => Interlocked.Increment(ref disposeCount)
                    )
                )
        );

        var baselineDisposeCount = Volatile.Read(ref disposeCount);
        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"isolated:{TestMcpTaskFeatureServerHost.RequiredToolName}",
            new Dictionary<string, object?> { ["value"] = "alpha" }
        );

        var completedTask = await WaitForTaskAsync(
            gatewayServer.Client,
            task.TaskId,
            McpTaskStatus.Completed
        );
        var taskResult = DeserializeToolResult(await gatewayServer.Client.GetTaskResultAsync(task.TaskId));
        await WaitForDisposeCountAsync(
            () => Volatile.Read(ref disposeCount),
            baselineDisposeCount + 1
        );

        await Assert.That(completedTask.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(GetSingleText(taskResult)).IsEqualTo("required:alpha");
    }

    [Test]
    public async Task CallToolAsTaskAsync_CancelledLocalTaskReleasesIsolatedBinding()
    {
        var disposeCount = 0;
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
                    new TrackingTaskBindingResolver(
                        serviceProvider.GetRequiredService<IMcpGatewayFactory>(),
                        options =>
                            options.AddTool(
                                "isolated",
                                TestFunctionFactory.CreateFunction(
                                    (string value, CancellationToken cancellationToken) =>
                                        RunLocalCancellableTaskToolAsync(value, cancellationToken),
                                    LocalCancellableTaskToolName,
                                    "Runs a cancellable local task."
                                )
                            ),
                        () => Interlocked.Increment(ref disposeCount)
                    )
                )
        );

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"isolated:{LocalCancellableTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "beta" }
        );
        var initialDisposeCount = Volatile.Read(ref disposeCount);

        var cancelledTask = await gatewayServer.Client.CancelTaskAsync(task.TaskId);
        await WaitForDisposeCountAsync(
            () => Volatile.Read(ref disposeCount),
            initialDisposeCount + 1
        );
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(cancelledTask.Status).IsEqualTo(McpTaskStatus.Cancelled);
        await Assert.That(trackedTask?.Status).IsEqualTo(McpTaskStatus.Cancelled);
    }

    [Test]
    public async Task RemoveSessionAsync_CancelsActiveLocalTaskAndReleasesBinding()
    {
        var disposeCount = 0;
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
                    new TrackingTaskBindingResolver(
                        serviceProvider.GetRequiredService<IMcpGatewayFactory>(),
                        options =>
                            options.AddTool(
                                "isolated",
                                TestFunctionFactory.CreateFunction(
                                    (string value, CancellationToken cancellationToken) =>
                                        RunLocalCancellableTaskToolAsync(value, cancellationToken),
                                    LocalCancellableTaskToolName,
                                    "Runs a cancellable local task."
                                )
                            ),
                        () => Interlocked.Increment(ref disposeCount)
                    )
                )
        );

        _ = await gatewayServer.Client.CallToolAsTaskAsync(
            $"isolated:{LocalCancellableTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "gamma" }
        );
        var initialDisposeCount = Volatile.Read(ref disposeCount);
        var taskStore = gatewayServer.GetRequiredService<McpGatewayMcpServerTaskStore>();

        await taskStore
            .RemoveSessionAsync(gatewayServer.Server.SessionId ?? string.Empty)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForDisposeCountAsync(
            () => Volatile.Read(ref disposeCount),
            initialDisposeCount + 1
        );
    }

    private static async Task<McpTask> WaitForTaskAsync(
        ModelContextProtocol.Client.McpClient client,
        string taskId,
        McpTaskStatus expectedStatus
    )
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var task = await client.GetTaskAsync(taskId);
            if (task?.Status == expectedStatus)
            {
                return task;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException($"Task '{taskId}' did not reach '{expectedStatus}'.");
    }

    private static async Task WaitForDisposeCountAsync(Func<int> getCount, int expectedCount)
    {
        var actualCount = getCount();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (actualCount >= expectedCount)
            {
                return;
            }

            actualCount = getCount();
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException(
            $"The expected dispose count was not reached in time. Expected at least '{expectedCount}', actual '{actualCount}'."
        );
    }

    private static async Task<string> RunLocalCancellableTaskToolAsync(
        string value,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return $"local-cancellable:{value}";
    }

    private static CallToolResult DeserializeToolResult(JsonElement result) =>
        JsonSerializer.Deserialize<CallToolResult>(result.GetRawText(), McpGatewayJsonSerializer.Options)
        ?? throw new InvalidOperationException("Task result payload was not a CallToolResult.");

    private static string GetSingleText(CallToolResult result) =>
        ((TextContentBlock)result.Content.Single()).Text;

    private sealed class TrackingTaskBindingResolver(
        IMcpGatewayFactory gatewayFactory,
        Action<McpGatewayOptions> configure,
        Action onDispose
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

            var gatewayInstance = gatewayFactory.Create(configure);
            return ValueTask.FromResult<IMcpGatewayServerBinding>(
                new McpGatewayServerBinding(
                    gatewayInstance.Gateway,
                    gatewayInstance.PromptCatalog,
                    gatewayInstance.ResourceCatalog,
                    gatewayInstance.Registry,
                    disposeAsync: async () =>
                    {
                        onDispose();
                        await gatewayInstance.DisposeAsync();
                    }
                )
            );
        }
    }
}

#pragma warning restore MCPEXP001
