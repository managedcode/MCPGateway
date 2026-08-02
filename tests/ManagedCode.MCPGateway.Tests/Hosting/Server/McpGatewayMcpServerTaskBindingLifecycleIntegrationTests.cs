using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskBindingLifecycleIntegrationTests
{
    private const string LocalCancellableTaskToolName = "isolated_local_cancellable_task";

    [Test]
    public async Task CompletedTask_ReleasesItsIsolatedBindingAndKeepsTheResultPollable()
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
                            options.AddMcpClient(
                                "isolated",
                                upstreamServer.Client,
                                disposeClient: false
                            ),
                        () => Interlocked.Increment(ref disposeCount)
                    )
                )
        );

        var baselineDisposeCount = Volatile.Read(ref disposeCount);
        var created = await StartTaskAsync(
            gatewayServer.Client,
            TestMcpTaskFeatureServerHost.RequiredToolName,
            "alpha"
        );
        var completed = await WaitForTaskAsync<CompletedTaskResult>(
            gatewayServer.Client,
            created.TaskId
        );
        await WaitForDisposeCountAsync(
            () => Volatile.Read(ref disposeCount),
            baselineDisposeCount + 1
        );
        var polledAgain = await gatewayServer.Client.GetTaskAsync(created.TaskId);

        await Assert.That(GetSingleText(DeserializeToolResult(completed.Result))).IsEqualTo("required:alpha");
        await Assert.That(polledAgain).IsTypeOf<CompletedTaskResult>();
    }

    [Test]
    public async Task CancelledTask_ReleasesItsIsolatedBindingAndKeepsCancelledStatePollable()
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

        var created = await StartTaskAsync(
            gatewayServer.Client,
            LocalCancellableTaskToolName,
            "beta"
        );
        var initialDisposeCount = Volatile.Read(ref disposeCount);
        _ = await gatewayServer.Client.CancelTaskAsync(created.TaskId);
        var cancelled = await WaitForTaskAsync<CancelledTaskResult>(
            gatewayServer.Client,
            created.TaskId
        );
        await WaitForDisposeCountAsync(
            () => Volatile.Read(ref disposeCount),
            initialDisposeCount + 1
        );

        await Assert.That(cancelled.Status).IsEqualTo(McpTaskStatus.Cancelled);
    }

    private static async Task<CreateTaskResult> StartTaskAsync(
        McpClient client,
        string toolName,
        string value
    )
    {
        var result = await client.CallToolAsTaskAsync(
            new CallToolRequestParams
            {
                Name = toolName,
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["value"] = JsonSerializer.SerializeToElement(value),
                },
            }
        );

        return result.TaskCreated
            ?? throw new InvalidOperationException($"Tool '{toolName}' did not create an MCP task.");
    }

    private static async Task<TTask> WaitForTaskAsync<TTask>(McpClient client, string taskId)
        where TTask : GetTaskResult
    {
        const int maximumAttempts = 200;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var task = await client.GetTaskAsync(taskId);
            if (task is TTask expected)
            {
                return expected;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException(
            $"Task '{taskId}' did not reach '{typeof(TTask).Name}' in time."
        );
    }

    private static async Task WaitForDisposeCountAsync(Func<int> getCount, int expectedCount)
    {
        const int maximumAttempts = 200;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var actualCount = getCount();
            if (actualCount >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException(
            $"The expected dispose count was not reached. Expected at least '{expectedCount}', actual '{getCount()}'."
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
        result.Deserialize<CallToolResult>(McpGatewayJsonSerializer.Options)
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
